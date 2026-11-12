using Fleet.Orchestrator.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Services.Push;

public static class PushServiceCollectionExtensions
{
    /// <summary>
    /// Push notifications to the first-party iOS app (off by default). <see cref="PushReadiness"/>
    /// reads the key once when the feature is on, and the notifier and <c>/api/push</c> both follow
    /// its answer; the client is built only when it says the sender can run.
    /// </summary>
    public static IServiceCollection AddPushNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PushOptions>(configuration.GetSection(PushOptions.Section));
        services.AddSingleton<PushReadiness>();
        services.AddSingleton<OperatorStops>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var push = sp.GetRequiredService<IOptions<PushOptions>>().Value;
            return new ApnsProviderToken(push.ApnsKeyBase64, push.ApnsKeyId, push.ApnsTeamId);
        });
        services.AddHttpClient(ApnsClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15))
            // The factory's own loggers write the request URI at Information, and the device token
            // is in its path. Without them nothing on this client logs a token.
            .RemoveAllLoggers();
        services.AddSingleton<IApnsClient>(sp => new ApnsClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApnsClient.HttpClientName),
            sp.GetRequiredService<IOptions<PushOptions>>(),
            sp.GetRequiredService<ApnsProviderToken>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<PushNotifierService>();
        return services;
    }
}
