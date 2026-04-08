namespace Fleet.Temporal.Configuration;

public class AuthTokenRefreshOptions
{
    public const string Section = "AuthTokenRefresh";

    public string ClaudeClientId { get; set; } = "";
    public string CodexClientId { get; set; } = "";
}
