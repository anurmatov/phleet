using Fleet.Shared;

namespace Fleet.Agent.Services.HostedProviders;

/// <summary>
/// Holds the hosted provider's API key in memory, and nowhere else (#335 D5.4).
/// </summary>
/// <remarks>
/// <para>
/// <c>entrypoint.sh</c> writes the key to <see cref="HostedModelProviders.KeyFilePath"/> (mode
/// 0400) and unsets it from the environment before <c>exec dotnet</c>, so PID 1 never carries it.
/// <see cref="Load"/> reads that file once and deletes it. After startup the key exists in no
/// environment block, no file and no child process — only in this object, which only the loopback
/// adapter reads.
/// </para>
/// <para>
/// A failed delete is a startup failure, not a warning: a key file left behind is readable from
/// the model's own shell.
/// </para>
/// </remarks>
public sealed class HostedProviderKeyStore
{
    private readonly string _path;
    private string? _key;

    public HostedProviderKeyStore() : this(HostedModelProviders.KeyFilePath)
    {
    }

    internal HostedProviderKeyStore(string path) => _path = path;

    /// <summary>True once <see cref="Load"/> has succeeded.</summary>
    public bool IsLoaded => _key is not null;

    /// <summary>The key. Throws if <see cref="Load"/> has not succeeded.</summary>
    public string Key => _key ?? throw new InvalidOperationException(
        "HostedProviderKeyStore: the hosted provider key has not been loaded.");

    /// <summary>
    /// Reads the key file, validates it, deletes it and keeps the value. Idempotent once loaded.
    /// Throws, naming <paramref name="keyEnvVar"/> but never the value, on any fault.
    /// </summary>
    public void Load(string keyEnvVar)
    {
        if (_key is not null)
            return;

        if (!File.Exists(_path))
            throw new InvalidOperationException(
                $"{HostedModelProviders.DescribeKeyFault(keyEnvVar, null)} Expected the key file "
                + $"{_path}, written by entrypoint.sh; it is missing.");

        string raw;
        try
        {
            raw = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not read the {keyEnvVar} key file {_path}: {ex.GetType().Name}.");
        }

        // Delete before validating, so a bad key does not stay on disk either.
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not delete the {keyEnvVar} key file {_path}: {ex.GetType().Name}. "
                + "Refusing to start with the key left on disk.");
        }

        if (File.Exists(_path))
            throw new InvalidOperationException(
                $"The {keyEnvVar} key file {_path} still exists after delete. "
                + "Refusing to start with the key left on disk.");

        var value = raw.Trim();
        if (HostedModelProviders.DescribeKeyFault(keyEnvVar, value) is { } fault)
            throw new InvalidOperationException(fault);

        _key = value;
    }
}
