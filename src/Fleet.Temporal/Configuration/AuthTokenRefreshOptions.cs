namespace Fleet.Temporal.Configuration;

public class AuthTokenRefreshOptions
{
    public const string Section = "AuthTokenRefresh";

    public string ClaudeClientId { get; set; } = "";
    public string CodexClientId { get; set; } = "";

    // Gemini OAuth installed-app constants extracted by setup.sh from the host's
    // @google/gemini-cli npm bundle. The CLI ships these in plaintext (chunk-B6PIKVSF.js
    // exports OAUTH_CLIENT_ID and OAUTH_CLIENT_SECRET); they are public installed-app
    // identifiers in the same vein as gcloud / GitHub CLI ship — not cryptographic
    // secrets — but GitHub's secret scanner rejects the GOCSPX- prefix if hardcoded
    // in the repo, so they live in cluster .env and are extracted at install time.
    public string GeminiClientId { get; set; } = "";
    public string GeminiClientSecret { get; set; } = "";
}
