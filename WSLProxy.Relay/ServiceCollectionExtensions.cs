using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Proxy;
using WSLProxy.Relay.Status;

namespace WSLProxy.Relay;

/// <summary>Registers the relay with a Generic Host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="RelayOptions"/> from the <c>Relay</c> section of <paramref name="configuration"/>, and registers one
    /// <see cref="RelayService"/> as a hosted service, <see cref="IRelayStatus"/> and <see cref="IRelayControl"/>.
    /// </summary>
    /// <remarks>
    /// The options aren't validated through the options system: a validation failure during a settings-file reload would be
    /// thrown on the file watcher's thread. <see cref="RelayService"/> validates each version itself, keeps the last good one
    /// and reports the problem in its status instead.
    /// </remarks>
    public static IServiceCollection AddRelay(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RelayOptions>().Bind(configuration.GetSection(RelayOptions.SectionName));
        services.AddSingleton<RelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayService>());
        services.AddSingleton<IRelayStatus>(sp => sp.GetRequiredService<RelayService>());
        services.AddSingleton<IRelayControl>(sp => sp.GetRequiredService<RelayService>());
        return services;
    }

    /// <summary>Validates relay options the same way the relay does, for example before the UI saves the user's settings.</summary>
    /// <returns>The problems found; empty when the options are valid.</returns>
    public static IReadOnlyList<string> ValidateRelayOptions(RelayOptions options)
    {
        var result = new RelayOptionsValidator().Validate(null, options);
        return result.Failed ? [.. result.Failures] : [];
    }
}
