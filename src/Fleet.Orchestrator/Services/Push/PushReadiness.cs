using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services.Push;

/// <summary>
/// Whether the sender can run, decided once from the settings and from the key itself.
/// </summary>
/// <remarks>
/// The notifier and <c>/api/push</c> both read this, so a phone is never told notifications are on
/// while the sender is off. Off, the key is not read at all.
/// </remarks>
public sealed class PushReadiness
{
    public PushReadiness(IOptions<PushOptions> options)
    {
        var o = options.Value;
        Enabled = o.Enabled;
        if (Enabled) Fault = o.DescribeFault() ?? KeyFault(o.ApnsKeyBase64);
    }

    public bool Enabled { get; }

    /// <summary>What stops an enabled sender, or <c>null</c>. Names the setting or the key's problem, never a value.</summary>
    public string? Fault { get; }

    /// <summary>On and able to sign: the only state in which a device may register.</summary>
    public bool IsReady => Enabled && Fault is null;

    private static string? KeyFault(string key)
    {
        try
        {
            using var _ = ApnsProviderToken.ImportKey(key);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }
}
