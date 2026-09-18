using Microsoft.Extensions.Configuration;

namespace Fleet.Comms.Configuration;

/// <summary>
/// The one place <see cref="CommsOptions"/> is resolved, shared by the web host and the operator
/// commands.
///
/// <para><b>Two resolutions is one resolution too many.</b> The CLI previously built its own
/// configuration from a different content root and a shorter list of sources, so an
/// <c>appsettings.{Environment}.json</c> could point the operator and the running service at
/// different databases — the operator issues a code the service rejects, backs up a database nobody
/// serves, or revokes a device outside the live store, with both files present and no error
/// anywhere. Sharing the resolution makes that class of divergence unrepresentable rather than
/// merely unlikely.</para>
///
/// <para>The content root is the process working directory, which is what
/// <c>WebApplication.CreateBuilder</c> uses by default — so the host and a subcommand launched into
/// the same container agree by construction.</para>
/// </summary>
public static class CommsConfiguration
{
    /// <summary>The environment name, resolved exactly as the generic host resolves it.</summary>
    public static string EnvironmentName =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    /// <summary>
    /// The content root, resolved the way the generic host resolves it.
    ///
    /// <para>Using the working directory alone was not actually sharing: a deployment that sets
    /// <c>ASPNETCORE_CONTENTROOT</c> or <c>DOTNET_CONTENTROOT</c>, or passes <c>--contentRoot</c>,
    /// moves the host's <c>appsettings</c> lookup and left a subcommand reading a different file —
    /// the divergence this class exists to remove, through a door one step further back.</para>
    /// </summary>
    public static string ContentRoot => ResolveContentRoot(Environment.GetCommandLineArgs());

    /// <summary>
    /// The host's precedence, <b>measured against the host rather than reasoned about</b>:
    /// <c>--contentRoot</c> on the command line, then <c>DOTNET_CONTENTROOT</c>, then
    /// <c>ASPNETCORE_CONTENTROOT</c>, then the working directory.
    ///
    /// <para><c>DOTNET_</c> beating <c>ASPNETCORE_</c> is the opposite of what the prefix-layering
    /// order suggests, and it is what a probe against a real <c>WebApplication.CreateBuilder</c>
    /// actually reports. An earlier version of this method had the two the other way round, which
    /// is worse than not checking them at all: a deployment setting both would have had its
    /// service and its operator commands reading different <c>appsettings</c> files while this
    /// class claimed to have made that impossible.</para>
    /// </summary>
    public static string ResolveContentRoot(string[] commandLine)
    {
        var configured =
            FindContentRootArgument(commandLine)
            ?? Environment.GetEnvironmentVariable("DOTNET_CONTENTROOT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT");

        return string.IsNullOrWhiteSpace(configured)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(configured);
    }

    private static string? FindContentRootArgument(string[] args)
    {
        // Both spellings the host accepts: `--contentRoot <path>` and `--contentRoot=<path>`,
        // case-insensitively, as configuration keys are matched.
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument.StartsWith("--contentRoot=", StringComparison.OrdinalIgnoreCase))
                return argument["--contentRoot=".Length..];

            if (argument.Equals("--contentRoot", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>Build configuration from the same root and the same sources, in the same order.</summary>
    public static IConfigurationRoot Build() =>
        new ConfigurationBuilder()
            .SetBasePath(ContentRoot)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>Bind the options a subcommand needs, without constructing a listener.</summary>
    public static CommsOptions Resolve()
    {
        var options = new CommsOptions();
        Build().GetSection(CommsOptions.SectionName).Bind(options);
        return options;
    }
}
