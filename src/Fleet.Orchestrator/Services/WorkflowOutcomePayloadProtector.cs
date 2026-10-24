using Microsoft.AspNetCore.DataProtection;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Encrypts workflow-outcome evidence at rest (#476). Keys live in the operator-owned persisted
/// keyring directory; a missing or wrong key fails closed (the ciphertext is retained, nothing is
/// logged and there is no plaintext fallback).
/// </summary>
public interface IWorkflowOutcomePayloadProtector
{
    byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext);

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the key is unavailable.</summary>
    byte[] Unprotect(string purpose, byte[] ciphertext);
}

public sealed class DataProtectionWorkflowOutcomePayloadProtector(IDataProtectionProvider provider) : IWorkflowOutcomePayloadProtector
{
    public const string PurposeRoot = "Fleet.WorkflowOutcomes.v1";

    public byte[] Protect(string purpose, ReadOnlySpan<byte> plaintext) =>
        provider.CreateProtector(PurposeRoot, purpose).Protect(plaintext.ToArray());

    public byte[] Unprotect(string purpose, byte[] ciphertext) =>
        provider.CreateProtector(PurposeRoot, purpose).Unprotect(ciphertext);
}
