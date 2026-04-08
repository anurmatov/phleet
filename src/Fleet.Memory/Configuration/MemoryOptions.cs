namespace Fleet.Memory.Configuration;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    public required string Path { get; set; }
    public int PollingIntervalSeconds { get; set; } = 30;
}

public sealed class QdrantOptions
{
    public const string Section = "Qdrant";

    public required string Url { get; set; }
    public string Collection { get; set; } = "fleet_memories";
    /// <summary>
    /// Cosine similarity threshold (0-1) for flagging potential duplicates on store.
    /// Memories above this score trigger a hint in the response. Set to 0 to disable.
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.85f;
}

public sealed class EmbeddingOptions
{
    public const string Section = "Embedding";

    /// <summary>Embedding provider: "onnx" (default, zero-dependency) or "ollama".</summary>
    public string Provider { get; set; } = "onnx";

    /// <summary>Output vector dimensions. Must match the chosen provider's model.</summary>
    public int Dimensions { get; set; } = 384;

    public OnnxSubOptions Onnx { get; set; } = new();
    public OllamaSubOptions Ollama { get; set; } = new();
}

public sealed class OnnxSubOptions
{
    public string ModelPath { get; set; } = "./models/all-MiniLM-L6-v2.onnx";
    public string VocabPath { get; set; } = "./models/all-MiniLM-L6-v2-vocab.txt";
}

public sealed class OllamaSubOptions
{
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "nomic-embed-text";
}
