namespace Fleet.Orchestrator.Services;

/// <summary>
/// Use-site read helper for .env values that may change at runtime without a container restart.
/// All keys with bootstrapOnly: false MUST be read via this interface, never via IConfiguration
/// or Environment.GetEnvironmentVariable (both are captured at container-start time).
///
/// The implementation caches the parsed .env with a 30-second TTL.
/// Call InvalidateCache() synchronously before propagation to ensure the next Get() call
/// reflects the value just written by PUT /api/credentials/{key}.
/// </summary>
public interface ICredentialsReader
{
    /// <summary>
    /// Returns the current value of <paramref name="key"/> from .env, or null if the key is
    /// absent, the file is unreadable, or the value is empty after quote-stripping.
    /// "Key absent" and "file unreadable" are deliberately conflated — both mean "not set."
    /// </summary>
    string? Get(string key);

    /// <summary>
    /// Clears the in-memory cache immediately so the next Get() call re-reads from disk.
    /// Called synchronously by the credentials save handler before propagation starts.
    /// </summary>
    void InvalidateCache();
}
