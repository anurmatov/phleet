using Fleet.Shared.Security;
using Fleet.Memory.Configuration;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Services;

internal sealed record VerifiedMemoryRequest(VerifiedAgentIdentity Identity, VerifiedAclSnapshot Snapshot)
{
    private static readonly object Key = new();
    internal static bool IsRequired(HttpContext? context)
    {
        var options = context?.RequestServices?.GetService(typeof(IOptions<FleetIdentityOptions>)) as IOptions<FleetIdentityOptions>;
        if (options is not null && !options.Value.Enabled) return false;
        return context is not null && context.Connection.LocalPort == (options?.Value.VerifiedPort ?? 3101);
    }
    internal static VerifiedMemoryRequest? Get(HttpContext? context) => context?.Items[Key] as VerifiedMemoryRequest;
    internal static void Clear(HttpContext context) => context.Items.Remove(Key);
    internal static void Set(HttpContext context, VerifiedMemoryRequest request) => context.Items[Key] = request;
}
