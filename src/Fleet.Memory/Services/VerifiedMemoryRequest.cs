using Fleet.Shared.Security;

namespace Fleet.Memory.Services;

internal sealed record VerifiedMemoryRequest(VerifiedAgentIdentity Identity, VerifiedAclSnapshot Snapshot)
{
    private static readonly object Key = new();
    internal static VerifiedMemoryRequest? Get(HttpContext? context) => context?.Items[Key] as VerifiedMemoryRequest;
    internal static void Clear(HttpContext context) => context.Items.Remove(Key);
    internal static void Set(HttpContext context, VerifiedMemoryRequest request) => context.Items[Key] = request;
}
