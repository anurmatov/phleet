using System.Security.Cryptography;
using System.Text;
using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Fleet.Memory.Data;

public sealed class VectorStore(IOptions<QdrantOptions> qdrantOptions, IOptions<EmbeddingOptions> embeddingOptions, ILogger<VectorStore> logger) : IVectorIndex
{
    private const string DenseVectorName = "dense";
    private const string SparseVectorName = "sparse";

    // Points per upsert call. A memory can have a few hundred sections, each with a dense vector.
    private const int UpsertBatch = 50;
    private const uint ScrollPage = 500;

    private readonly QdrantClient _client = CreateClient(qdrantOptions.Value.Url);
    private readonly string _collection = qdrantOptions.Value.Collection;
    private readonly int _dimensions = embeddingOptions.Value.Dimensions;

    private static QdrantClient CreateClient(string url)
    {
        var uri = new Uri(url);
        return new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == "https");
    }

    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        var collections = await _client.ListCollectionsAsync(ct);
        if (collections.Any(c => c == _collection))
        {
            // Check if collection has sparse vectors and correct dense dimensions — recreate if not
            var info = await _client.GetCollectionInfoAsync(_collection, ct);
            var lacksSparse = info.Config.Params.SparseVectorsConfig is null ||
                              info.Config.Params.SparseVectorsConfig.Map.Count == 0;
            var existingDims = info.Config.Params.VectorsConfig?.Params?.Size
                               ?? info.Config.Params.VectorsConfig?.ParamsMap?.Map
                                   .GetValueOrDefault(DenseVectorName)?.Size;
            var dimensionMismatch = existingDims.HasValue && (int)existingDims.Value != _dimensions;

            if (lacksSparse)
            {
                logger.LogInformation("Collection '{Collection}' exists but lacks sparse vectors, recreating for hybrid search", _collection);
                await _client.DeleteCollectionAsync(_collection, cancellationToken: ct);
            }
            else if (dimensionMismatch)
            {
                logger.LogWarning("Collection '{Collection}' has {Existing}d but configured for {Expected}d — recreating (data will be re-indexed on startup)", _collection, existingDims!.Value, _dimensions);
                await _client.DeleteCollectionAsync(_collection, cancellationToken: ct);
            }
            else
            {
                logger.LogInformation("Qdrant collection '{Collection}' already exists with hybrid search", _collection);
                return;
            }
        }

        await _client.CreateCollectionAsync(
            _collection,
            vectorsConfig: new VectorParamsMap
            {
                Map =
                {
                    [DenseVectorName] = new VectorParams { Size = (ulong)_dimensions, Distance = Distance.Cosine }
                }
            },
            sparseVectorsConfig: new SparseVectorConfig
            {
                Map =
                {
                    [SparseVectorName] = new SparseVectorParams
                    {
                        Modifier = Modifier.Idf
                    }
                }
            },
            cancellationToken: ct);

        // Create payload indexes for filtering
        await _client.CreatePayloadIndexAsync(_collection, "agent_name", PayloadSchemaType.Keyword, cancellationToken: ct);
        await _client.CreatePayloadIndexAsync(_collection, "memory_type", PayloadSchemaType.Keyword, cancellationToken: ct);
        await _client.CreatePayloadIndexAsync(_collection, "project", PayloadSchemaType.Keyword, cancellationToken: ct);
        await _client.CreatePayloadIndexAsync(_collection, "tags", PayloadSchemaType.Keyword, cancellationToken: ct);

        logger.LogInformation("Created Qdrant collection '{Collection}' with hybrid search (dense {Dimensions}d + sparse BM25)", _collection, _dimensions);
    }

    public async Task UpsertAsync(IReadOnlyList<IndexPoint> points, CancellationToken ct = default)
    {
        foreach (var batch in points.Chunk(UpsertBatch))
        {
            await _client.UpsertAsync(_collection, batch.Select(BuildPoint).ToList(), cancellationToken: ct);
            logger.LogDebug("Upserted {Count} point(s) to Qdrant (dense + sparse)", batch.Length);
        }
    }

    /// <summary>
    /// The id of a section's point. The first section has the id the memory's one point had before
    /// memories were indexed in sections, so indexing a memory again overwrites that point instead
    /// of leaving it beside the new ones.
    /// </summary>
    internal static Guid PointIdFor(string filePathKey, int sectionIndex) =>
        DeterministicGuid(sectionIndex == 0 ? filePathKey : $"{filePathKey}#section:{sectionIndex}");

    internal static PointStruct BuildPoint(IndexPoint source)
    {
        var (sparseIndices, sparseValues) = Bm25Tokenizer.Tokenize(source.SparseText);

        var point = new PointStruct
        {
            Id = new PointId { Uuid = PointIdFor(source.FilePathKey, source.SectionIndex).ToString() },
            Vectors = new Vectors
            {
                Vectors_ = new NamedVectors()
            }
        };

        // Dense vector
        var denseVector = new Vector();
        denseVector.Data.AddRange(source.Dense);
        point.Vectors.Vectors_.Vectors[DenseVectorName] = denseVector;

        // Sparse vector
        if (sparseIndices.Length > 0)
        {
            var sparseVector = new Vector
            {
                Indices = new SparseIndices()
            };
            sparseVector.Data.AddRange(sparseValues);
            sparseVector.Indices.Data.AddRange(sparseIndices);
            point.Vectors.Vectors_.Vectors[SparseVectorName] = sparseVector;
        }

        foreach (var (key, value) in source.Payload)
        {
            point.Payload[key] = value switch
            {
                string s => s,
                int i => i,
                long l => l,
                double d => d,
                bool b => b,
                IEnumerable<string> list => list.ToArray(),
                IEnumerable<object> list => list.Select(x => x.ToString() ?? "").ToArray(),
                _ => value.ToString() ?? ""
            };
        }

        return point;
    }

    public async Task<List<IndexMatch>> SearchDenseAsync(float[] queryEmbedding, int limit, IndexFilter? filter = null, CancellationToken ct = default)
    {
        var results = await _client.QueryAsync(
            _collection,
            query: queryEmbedding,
            usingVector: DenseVectorName,
            filter: BuildFilter(filter),
            limit: (ulong)limit,
            payloadSelector: MatchPayload,
            cancellationToken: ct);

        return ToMatches(results);
    }

    public async Task<List<IndexMatch>> SearchSparseAsync(string queryText, int limit, IndexFilter? filter = null, CancellationToken ct = default)
    {
        // Sparse query vector from BM25 tokenization. No keyword in the query: nothing can match.
        var (sparseIndices, sparseValues) = Bm25Tokenizer.Tokenize(queryText);
        if (sparseIndices.Length == 0)
            return [];

        var query = new Query { Nearest = new VectorInput { Sparse = new SparseVector() } };
        query.Nearest.Sparse.Values.AddRange(sparseValues);
        query.Nearest.Sparse.Indices.AddRange(sparseIndices);

        var results = await _client.QueryAsync(
            _collection,
            query: query,
            usingVector: SparseVectorName,
            filter: BuildFilter(filter),
            limit: (ulong)limit,
            payloadSelector: MatchPayload,
            cancellationToken: ct);

        // A point that shares no keyword with the query is not a keyword hit, whatever came back.
        return ToMatches(results.Where(r => r.Score > 0));
    }

    private static WithPayloadSelector MatchPayload => new()
    {
        Include = new PayloadIncludeSelector
        {
            Fields = { IndexPayload.FilePath, IndexPayload.SectionStart, IndexPayload.SectionLength }
        }
    };

    private static List<IndexMatch> ToMatches(IEnumerable<ScoredPoint> points) =>
        points
            .Where(r => r.Payload.ContainsKey(IndexPayload.FilePath))
            .Select(r => new IndexMatch(
                r.Payload[IndexPayload.FilePath].StringValue,
                r.Score,
                (int)Integer(r.Payload, IndexPayload.SectionStart, 0),
                (int)Integer(r.Payload, IndexPayload.SectionLength, -1)))
            .ToList();

    private static long Integer(IDictionary<string, Value> payload, string key, long missing) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.IntegerValue
            ? value.IntegerValue
            : missing;

    private static Condition Keyword(string key, string value) => new()
    {
        Field = new FieldCondition { Key = key, Match = new Match { Keyword = value } }
    };

    /// <summary>The Qdrant filter for a search: every keyword must match, and the updated time must not be earlier.</summary>
    internal static Filter? BuildFilter(IndexFilter? filter)
    {
        if (filter is null || filter.IsEmpty)
            return null;

        var result = new Filter();
        foreach (var (key, value) in filter.Keywords)
            result.Must.Add(Keyword(key, value));

        if (filter.UpdatedSince is { } since)
        {
            result.Must.Add(new Condition
            {
                Field = new FieldCondition { Key = IndexPayload.UpdatedTs, Range = new Qdrant.Client.Grpc.Range { Gte = since } }
            });
        }

        return result;
    }

    /// <summary>
    /// The filter for a listing: the keywords, and only a memory's first point. A point from before
    /// sections has no section index, which a range never matches, so it is listed too.
    /// </summary>
    internal static Filter BuildListFilter(Dictionary<string, string>? filters)
    {
        var result = new Filter();
        foreach (var (key, value) in filters ?? [])
            result.Must.Add(Keyword(key, value));

        result.MustNot.Add(new Condition
        {
            Field = new FieldCondition { Key = IndexPayload.SectionIndex, Range = new Qdrant.Client.Grpc.Range { Gte = 1 } }
        });
        return result;
    }

    /// <summary>The filter for the points of one file, all of them or those not carrying a hash.</summary>
    internal static Filter BuildFileFilter(string filePathKey, string? exceptHash = null)
    {
        var result = new Filter();
        result.Must.Add(Keyword(IndexPayload.FilePath, filePathKey));
        if (exceptHash is not null)
            result.MustNot.Add(Keyword(IndexPayload.IndexHash, exceptHash));
        return result;
    }

    /// <summary>
    /// Scroll the points matching filters, returning metadata from payload: one entry per memory.
    /// Replaces filesystem scan for memory_list — uses qdrant's indexed payload fields.
    /// </summary>
    public async Task<List<Dictionary<string, string>>> ScrollFilteredAsync(Dictionary<string, string>? filters = null, CancellationToken ct = default)
    {
        var filter = BuildListFilter(filters);

        var results = new List<Dictionary<string, string>>();
        // The filter already leaves one point per memory; this keeps the promise if two are ever there.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        PointId? nextOffset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                _collection,
                filter: filter,
                payloadSelector: true,
                limit: ScrollPage,
                offset: nextOffset,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
                if (point.Payload.TryGetValue(IndexPayload.FilePath, out var path) && !seen.Add(path.StringValue))
                    continue;

                var entry = new Dictionary<string, string>();
                foreach (var (key, val) in point.Payload)
                    entry[key] = val.StringValue;

                // Tags come as a list value — join them
                if (point.Payload.TryGetValue("tags", out var tagsVal) && tagsVal.ListValue is not null)
                    entry["tags"] = string.Join(", ", tagsVal.ListValue.Values.Select(v => v.StringValue));

                results.Add(entry);
            }

            if (response.NextPageOffset is null)
                break;

            nextOffset = response.NextPageOffset;
        }

        return results;
    }

    public async Task DeleteAsync(string filePathKey, CancellationToken ct = default)
    {
        await _client.DeleteAsync(_collection, BuildFileFilter(filePathKey), cancellationToken: ct);
        logger.LogDebug("Deleted the points of {Path} from Qdrant", filePathKey);
    }

    public async Task DeleteStaleAsync(string filePathKey, string keepHash, CancellationToken ct = default)
    {
        await _client.DeleteAsync(_collection, BuildFileFilter(filePathKey, exceptHash: keepHash), cancellationToken: ct);
    }

    public async Task<List<IndexPointInfo>> ScrollIndexInfoAsync(CancellationToken ct = default)
    {
        var points = new List<IndexPointInfo>();
        var selector = new WithPayloadSelector
        {
            Include = new PayloadIncludeSelector
            {
                Fields =
                {
                    IndexPayload.FilePath, IndexPayload.IndexHash, IndexPayload.IndexFormat,
                    IndexPayload.SectionIndex, IndexPayload.SectionCount
                }
            }
        };
        PointId? nextOffset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                _collection,
                filter: null,
                payloadSelector: selector,
                limit: ScrollPage,
                offset: nextOffset,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
                if (!point.Payload.TryGetValue(IndexPayload.FilePath, out var fp))
                    continue;

                points.Add(new IndexPointInfo(
                    fp.StringValue,
                    point.Payload.TryGetValue(IndexPayload.IndexHash, out var hash) ? hash.StringValue : "",
                    (int)Integer(point.Payload, IndexPayload.IndexFormat, 0),
                    (int)Integer(point.Payload, IndexPayload.SectionIndex, 0),
                    (int)Integer(point.Payload, IndexPayload.SectionCount, 1)));
            }

            if (response.NextPageOffset is null)
                break;

            nextOffset = response.NextPageOffset;
        }

        return points;
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Use first 16 bytes of SHA256 as a deterministic GUID
        return new Guid(hash.AsSpan(0, 16));
    }
}
