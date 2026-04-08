namespace Fleet.Temporal.Activities;

using Fleet.Temporal.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Temporalio.Activities;

/// <summary>
/// Serializes <see cref="FleetWorkflowOptions"/> as a <see cref="JsonElement"/> for use
/// by the <see cref="Engine.TemplateEngine"/> (<c>{{config.*}}</c> scope).
/// Returning a JsonElement keeps the config determinism-safe: replayed from history,
/// not re-read from the options object on each replay.
/// </summary>
public sealed class LoadWorkflowConfigActivity(IOptions<FleetWorkflowOptions> options)
{
    private readonly FleetWorkflowOptions _options = options.Value;

    [Activity("LoadWorkflowConfig")]
    public JsonElement Load()
        => JsonSerializer.SerializeToElement(_options);
}
