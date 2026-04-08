using Fleet.Memory.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Fleet.Memory.Services;

/// <summary>
/// Zero-dependency embedding service using ONNX Runtime + all-MiniLM-L6-v2 (384 dims).
/// Registered as singleton — InferenceSession creation is expensive and thread-safe for Run().
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly ILogger<OnnxEmbeddingService> _logger;

    public OnnxEmbeddingService(IOptions<EmbeddingOptions> options, ILogger<OnnxEmbeddingService> logger)
    {
        _logger = logger;
        _dimensions = options.Value.Dimensions;

        var onnxOpts = options.Value.Onnx;
        var modelPath = ResolvePath(onnxOpts.ModelPath);
        var vocabPath = ResolvePath(onnxOpts.VocabPath);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"ONNX model not found: {modelPath}. Run scripts/download-onnx-model.sh to download it.", modelPath);

        if (!File.Exists(vocabPath))
            throw new FileNotFoundException(
                $"Vocabulary file not found: {vocabPath}. Run scripts/download-onnx-model.sh to download it.", vocabPath);

        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true });

        _logger.LogInformation("[ONNX] Embedding service initialized: model={Model}, dims={Dimensions}", modelPath, _dimensions);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Tokenize — BertTokenizer adds [CLS] and [SEP] automatically
        var ids = _tokenizer.EncodeToIds(text, MaxTokens, out _, out _);
        var seqLen = ids.Count;

        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];
        var tokenTypeIds = new long[seqLen]; // zeros (single-segment)

        for (var i = 0; i < seqLen; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1L;
        }

        // Build ONNX tensors [batch=1, seq_len]
        var dims = new[] { 1, seqLen };
        var inputIdsTensor = new DenseTensor<long>(inputIds, dims);
        var attMaskTensor = new DenseTensor<long>(attentionMask, dims);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, dims);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor),
        };

        // Run inference — output: last_hidden_state shape [1, seq_len, hidden_dims]
        using var results = _session.Run(inputs);
        var output = results[0].AsTensor<float>();

        // Validate model output dimensions match configured Dimensions value
        var actualDims = output.Dimensions[2];
        if (actualDims != _dimensions)
            throw new InvalidOperationException(
                $"[ONNX] Model output has {actualDims} dimensions but Embedding:Dimensions is configured as {_dimensions}. " +
                $"Update appsettings.json to set Embedding:Dimensions={actualDims}.");

        // Mean pooling over attended tokens
        var embedding = new float[_dimensions];
        var validTokens = 0;
        for (var t = 0; t < seqLen; t++)
        {
            if (attentionMask[t] == 0) continue;
            validTokens++;
            for (var d = 0; d < _dimensions; d++)
                embedding[d] += output[0, t, d];
        }
        if (validTokens > 0)
            for (var d = 0; d < _dimensions; d++)
                embedding[d] /= validTokens;

        // L2 normalize
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm > 0f)
            for (var d = 0; d < _dimensions; d++)
                embedding[d] /= norm;

        _logger.LogDebug("[ONNX] Generated embedding with {Dimensions} dimensions", _dimensions);
        return Task.FromResult(embedding);
    }

    public void Dispose() => _session.Dispose();

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
