using System.Security.Cryptography;
using System.Text;
using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Fleet.Memory.Data;

public sealed class VectorStore(IOptions<QdrantOptions> qdrantOptions, IOptions<EmbeddingOptions> embeddingOptions, ILogger<VectorStore> logger)
{
    private const string DenseVectorName = "dense";
    private const string SparseVectorName = "sparse";

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

    public async Task UpsertAsync(string filePathKey, float[] denseEmbedding, string textForSparse, Dictionary<string, object> payload, CancellationToken ct = default)
    {
        var pointId = DeterministicGuid(filePathKey);
        var (sparseIndices, sparseValues) = Bm25Tokenizer.Tokenize(textForSparse);

        var point = new PointStruct
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = new Vectors
            {
                Vectors_ = new NamedVectors()
            }
        };

        // Dense vector
        var denseVector = new Vector();
        denseVector.Data.AddRange(denseEmbedding);
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

        foreach (var (key, value) in payload)
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

        await _client.UpsertAsync(_collection, [point], cancellationToken: ct);
        logger.LogDebug("Upserted point {PointId} to Qdrant (dense + sparse)", pointId);
    }

    public async Task<List<(string FilePathKey, float Score)>> SearchAsync(float[] queryEmbedding, string queryText, int limit = 10, Dictionary<string, string>? filters = null, CancellationToken ct = default)
    {
        Filter? filter = null;

        if (filters is { Count: > 0 })
        {
            var conditions = new List<Condition>();

            foreach (var (key, value) in filters)
            {
                conditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = key,
                        Match = new Match { Keyword = value }
                    }
                });
            }

            filter = new Filter();
            filter.Must.AddRange(conditions);
        }

        // Sparse query vector from BM25 tokenization
        var (sparseIndices, sparseValues) = Bm25Tokenizer.Tokenize(queryText);

        // Hybrid search: prefetch from both dense and sparse, fuse with RRF
        var prefetchQueries = new List<PrefetchQuery>
        {
            // Dense semantic search
            new()
            {
                Query = new Query
                {
                    Nearest = new VectorInput
                    {
                        Dense = new DenseVector()
                    }
                },
                Using = DenseVectorName,
                Limit = (ulong)(limit * 2),
                Filter = filter
            },
        };
        prefetchQueries[0].Query.Nearest.Dense.Data.AddRange(queryEmbedding);

        // Sparse keyword search (only if we have tokens)
        if (sparseIndices.Length > 0)
        {
            var sparsePrefetch = new PrefetchQuery
            {
                Query = new Query
                {
                    Nearest = new VectorInput
                    {
                        Sparse = new SparseVector()
                    }
                },
                Using = SparseVectorName,
                Limit = (ulong)(limit * 2),
                Filter = filter
            };
            sparsePrefetch.Query.Nearest.Sparse.Values.AddRange(sparseValues);
            sparsePrefetch.Query.Nearest.Sparse.Indices.AddRange(sparseIndices);
            prefetchQueries.Add(sparsePrefetch);
        }

        var results = await _client.QueryAsync(
            _collection,
            prefetch: prefetchQueries,
            query: new Query { Fusion = Fusion.Rrf },
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: ct);

        return results
            .Where(r => r.Payload.ContainsKey("file_path"))
            .Select(r => (r.Payload["file_path"].StringValue, r.Score))
            .ToList();
    }

    /// <summary>
    /// Dense-only vector search for semantic similarity detection (no BM25/sparse).
    /// Used for duplicate detection where we want "same meaning" not "same keywords".
    /// </summary>
    public async Task<List<(string FilePathKey, float Score)>> SearchDenseOnlyAsync(float[] queryEmbedding, int limit = 3, CancellationToken ct = default)
    {
        var results = await _client.QueryAsync(
            _collection,
            query: queryEmbedding,
            usingVector: DenseVectorName,
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: ct);

        return results
            .Where(r => r.Payload.ContainsKey("file_path"))
            .Select(r => (r.Payload["file_path"].StringValue, r.Score))
            .ToList();
    }

    /// <summary>
    /// Scroll all points matching filters, returning metadata from payload.
    /// Replaces filesystem scan for memory_list — uses qdrant's indexed payload fields.
    /// </summary>
    public async Task<List<Dictionary<string, string>>> ScrollFilteredAsync(Dictionary<string, string>? filters = null, CancellationToken ct = default)
    {
        Filter? filter = null;

        if (filters is { Count: > 0 })
        {
            var conditions = new List<Condition>();
            foreach (var (key, value) in filters)
            {
                conditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = key,
                        Match = new Match { Keyword = value }
                    }
                });
            }
            filter = new Filter();
            filter.Must.AddRange(conditions);
        }

        var results = new List<Dictionary<string, string>>();
        PointId? nextOffset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                _collection,
                filter: filter,
                payloadSelector: true,
                limit: 100,
                offset: nextOffset,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
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
        var pointId = DeterministicGuid(filePathKey);

        await _client.DeleteAsync(_collection, [pointId], cancellationToken: ct);
        logger.LogDebug("Deleted point {PointId} from Qdrant", pointId);
    }

    public async Task<HashSet<string>> GetAllFilePathKeysAsync(CancellationToken ct = default)
    {
        var keys = new HashSet<string>();
        PointId? nextOffset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                _collection,
                filter: null,
                payloadSelector: new WithPayloadSelector { Include = new PayloadIncludeSelector { Fields = { "file_path" } } },
                limit: 100,
                offset: nextOffset,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
                if (point.Payload.TryGetValue("file_path", out var fp))
                    keys.Add(fp.StringValue);
            }

            if (response.NextPageOffset is null)
                break;

            nextOffset = response.NextPageOffset;
        }

        return keys;
    }

    private static Guid DeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Use first 16 bytes of SHA256 as a deterministic GUID
        return new Guid(hash.AsSpan(0, 16));
    }
}
