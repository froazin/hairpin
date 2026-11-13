using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Hairpin.Engine.Configuration;
using Hairpin.Engine.Proxy;
using Hairpin.Engine.Status;

namespace Hairpin.Engine;

/// <summary>Registers the relay with a Generic Host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="RelayOptions"/> from the <c>Relay</c> section of <paramref name="configuration"/>, and registers one
    /// <see cref="RelayService"/> as a hosted service, <see cref="IRelayStatus"/> and <see cref="IRelayControl"/>.
    /// </summary>
    /// <remarks>
    /// Neither binding nor validation may throw: on a settings-file reload they run on the file watcher's thread, where an
    /// exception would end the process. A binding failure is recorded in <see cref="RelayOptions.ConfigurationError"/>, and
    /// <see cref="RelayService"/> validates each version itself, keeping the last good one and reporting the problem instead.
    /// </remarks>
    public static IServiceCollection AddRelay(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSafeOptions<RelayOptions>(configuration, RelayOptions.SectionName, (options, error) => options.ConfigurationError = error);
        services.AddSingleton<RelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RelayService>());
        services.AddSingleton<IRelayStatus>(sp => sp.GetRequiredService<RelayService>());
        services.AddSingleton<IRelayControl>(sp => sp.GetRequiredService<RelayService>());
        services.AddSingleton<IRelayMetrics>(sp => sp.GetRequiredService<RelayService>());
        return services;
    }

    /// <summary>
    /// Binds options from a configuration section, with reload support, without ever throwing. When binding fails (for example
    /// a number written as text), the options keep their defaults for the fields that failed and <paramref name="onError"/> is
    /// told why.
    /// </summary>
    public static IServiceCollection AddSafeOptions<TOptions>(this IServiceCollection services, IConfiguration configuration, string section,
        Action<TOptions, string>? onError = null)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TOptions>().Configure(options =>
        {
            try
            {
                configuration.GetSection(section).Bind(options);
            }
            catch (InvalidOperationException ex)
            {
                // The message comes from the user's own settings file, not from network peers, so it's safe to show.
                onError?.Invoke(options, "The " + section + " settings couldn't be read: " + ex.Message);
            }
        });
        services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(new ConfigurationChangeTokenSource<TOptions>(Options.DefaultName, configuration));
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
