namespace Fleet.Agent.Configuration;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public required string Name { get; set; }
    public required string Role { get; set; }
    public List<string> Projects { get; set; } = [];
    public string Model { get; set; } = "claude-sonnet-4-6";
    public List<string> AllowedTools { get; set; } = ["Read", "Write", "Edit", "Bash", "Glob", "Grep"];
    public string PermissionMode { get; set; } = "acceptEdits";
    public int MaxTurns { get; set; } = 50;
    public required string WorkDir { get; set; }
    public int MaxConcurrentTasks { get; set; } = 1;
    public int ProactiveIntervalMinutes { get; set; } = 0;
    public string GroupListenMode { get; set; } = "mention";
    public int GroupDebounceSeconds { get; set; } = 15;
    public string ShortName { get; set; } = "";
    public bool ShowStats { get; set; } = true;
    public bool PrefixMessages { get; set; } = false;
    /// <summary>
    /// When true, intermediate tool-use progress messages are not sent to Telegram.
    /// Only the final assistant text response is posted. Default: false (preserves existing behavior).
    /// </summary>
    public bool SuppressToolMessages { get; set; } = false;
    public string? Effort { get; set; }
    public string? JsonSchema { get; set; }
    public string? AgentsJson { get; set; }
    public int ToolArgsTruncateLength { get; set; } = 300;
    public string Provider { get; set; } = "claude";
    public string? CodexSandboxMode { get; set; }
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public List<long> AllowedUserIds { get; set; } = [];
    public List<long> AllowedGroupIds { get; set; } = [];
    public bool SendOnly { get; set; }

    /// <summary>Prompt injected when a message has images but no caption text. Default: "(image attached — please analyze)".</summary>
    public string DefaultImagePrompt { get; set; } = "(image attached — please analyze)";

    /// <summary>Maximum number of photos to collect from a single media group. Extras are dropped with a user-facing warning. Default: 10.</summary>
    public int MaxImagesPerGroup { get; set; } = 10;

    /// <summary>Maximum individual photo size in bytes; photos above this limit are skipped with a user-facing warning. Default: 10 MB.</summary>
    public int MaxImageBytes { get; set; } = 10_485_760;

    /// <summary>Hard-cap on total buffering time for a media group in milliseconds. If photos keep arriving past this limit, the group is force-flushed. Default: 10000 ms.</summary>
    public int MaxGroupBufferMs { get; set; } = 10_000;

    /// <summary>When true, each downloaded photo is written to disk and a path hint is injected into the message text so agent tools can reach the bytes. Default: true.</summary>
    public bool PersistAttachments { get; set; } = true;

    /// <summary>Directory where attachment files are written. Default: /workspace/attachments.</summary>
    public string AttachmentDir { get; set; } = "/workspace/attachments";

    /// <summary>Attachment files older than this many hours are deleted by the lazy sweeper (called on each photo write and once at startup). Default: 48.</summary>
    public int AttachmentRetentionHours { get; set; } = 48;

    /// <summary>Maximum PDF document size in bytes; documents above this limit are rejected with a user-facing warning and not passed to the LLM. Default: 32 MB (Claude SDK per-document limit).</summary>
    public int MaxDocumentBytes { get; set; } = 33_554_432; // 32 MB
}

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "";
    public string Exchange { get; set; } = "fleet.group";
}

public sealed class WhisperOptions
{
    public const string Section = "Whisper";

    /// <summary>Base URL of the fleet-whisper transcription service (e.g. http://fleet-whisper:8080).</summary>
    public string ServiceUrl { get; set; } = "";
}

public sealed class TtsOptions
{
    public const string Section = "Tts";

    /// <summary>Base URL of the Kokoro TTS service (e.g. http://fleet-kokoro-tts:8880).</summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>Kokoro voice ID to use for synthesis. Default: af_nova.</summary>
    public string Voice { get; set; } = "af_nova";
}
