using System.Security.Cryptography;
using System.Text;

namespace Fleet.Orchestrator.Services;

/// <summary>Startup-only operator credential. No development, read-method or unknown-child exemptions.</summary>
public static class OpenShellOperatorAuthorization
{
    public static IApplicationBuilder UseOpenShellOperatorAuthorization(this IApplicationBuilder app, string token)
    {
        var expected = string.IsNullOrWhiteSpace(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return app.Use(async (context, next) =>
        {
            if (!Protects(context.Request.Path)) { await next(context); return; }
            if (expected is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = "openshell_operator_auth_unconfigured" });
                return;
            }
            var headers = context.Request.Headers.Authorization;
            var value = headers.Count == 1 ? headers[0] : null;
            var valid = value is not null && Encoding.UTF8.GetByteCount(value) <= 4096
                && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var supplied = valid ? value![7..] : "";
            valid = valid && supplied.Length > 0 && !supplied.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ',');
            if (!valid || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "openshell_operator_unauthorized" });
                return;
            }
            await next(context);
        });
    }

    public static bool Protects(PathString path)
    {
        var parts = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("api", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[1].Equals("services", StringComparison.OrdinalIgnoreCase))
            return parts.Length >= 3 && parts[2].Equals("restart", StringComparison.OrdinalIgnoreCase);
        if (!parts[1].Equals("agents", StringComparison.OrdinalIgnoreCase)) return false;
        // Create/delete share paths with reads. Protect the whole route shape, not only POST/DELETE.
        if (parts.Length <= 3 || parts[2].Equals("reprovision-running", StringComparison.OrdinalIgnoreCase)) return true;
        return parts[3].ToLowerInvariant() is "start" or "stop" or "restart" or "restart-with-version"
            or "reprovision" or "provision" or "deprovision" or "openshell" or "execution-policy" or "secret-bindings";
    }
}
