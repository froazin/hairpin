using Microsoft.Extensions.Options;
using WSLProxy.Relay.Routing;

namespace WSLProxy.Relay.Configuration;

/// <summary>Validates <see cref="RelayOptions"/> when the app starts and whenever the settings change.</summary>
internal sealed class RelayOptionsValidator : IValidateOptions<RelayOptions>
{
    private const int OneHour = 3600;
    private const int OneDay = 86400;

    public ValidateOptionsResult Validate(string? name, RelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.ConfigurationError is not null)
        {
            return ValidateOptionsResult.Fail(options.ConfigurationError);
        }

        // 0 means "any free port". It's for tests; the app never ships it.
        if (options.ListenPort is < 0 or > 65535)
        {
            failures.Add("Relay:ListenPort must be from 1 to 65535.");
        }

        var upstream = options.Upstream;
        if (upstream.IsConfigured)
        {
            if (upstream.Port is < 1 or > 65535)
            {
                failures.Add("Relay:Upstream:Port must be from 1 to 65535.");
            }

            if (!HostNormaliser.TryNormalise(upstream.Host.Trim(), out var host, out var error))
            {
                failures.Add("Relay:Upstream:Host isn't a valid host name or IPv4 address: " + error);
            }
            else if (DestinationPolicy.IsDeniedHost(host, upstream.Port, options.ListenPort, denyLocal: false, out _))
            {
                failures.Add("Relay:Upstream points back at the relay itself.");
            }
        }

        if (upstream.HealthCheckIntervalSeconds is < 0 or > OneDay)
        {
            failures.Add("Relay:Upstream:HealthCheckIntervalSeconds must be from 0 to 86400.");
        }

        var limits = options.Limits;
        if (limits.MaxRequestLineBytes < 256 || limits.MaxHeaderBytes < 1024 || limits.MaxRequestLineBytes > limits.MaxHeaderBytes
            || limits.MaxHeaderBytes > 1024 * 1024 || limits.MaxHeaderCount < 1)
        {
            failures.Add("Relay:Limits header sizes are out of range.");
        }

        // Upper bounds keep every timeout well inside what CancelAfter and Task.Delay accept (about 49 days).
        if (limits.HeaderTimeoutSeconds is < 1 or > OneHour || limits.ConnectTimeoutSeconds is < 1 or > OneHour
            || limits.IdleTimeoutSeconds is < 1 or > OneDay || limits.TunnelIdleTimeoutSeconds is < 1 or > OneDay)
        {
            failures.Add("Relay:Limits timeouts must be at least 1 second, at most 3600 for the header and connect timeouts and at most 86400 for the others.");
        }

        if (limits.MaxConnections < 1)
        {
            failures.Add("Relay:Limits:MaxConnections must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
