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
