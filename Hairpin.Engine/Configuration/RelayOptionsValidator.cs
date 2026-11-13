using Microsoft.Extensions.Options;
using Hairpin.Engine.Routing;

namespace Hairpin.Engine.Configuration;

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
        ValidateProxy("Http", upstream.Http, upstream.ForHttp, options.ListenPort, failures);
        // An HTTPS proxy that's the HTTP one has just been checked: don't report the same problem twice.
        ValidateProxy("Https", upstream.Https, upstream.ForHttps == upstream.ForHttp ? null : upstream.ForHttps, options.ListenPort, failures);
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

    /// <summary>Checks one proxy's own values, then the endpoint it resolves to, which may borrow the HTTP proxy's.</summary>
    private static void ValidateProxy(string name, UpstreamProxyOptions proxy, UpstreamEndpoint? endpoint, int listenPort, List<string> failures)
    {
        var key = "Relay:Upstream:" + name;
        if (proxy.Port is < 1 or > 65535)
        {
            failures.Add(key + ":Port must be from 1 to 65535.");
        }

        if (!string.IsNullOrWhiteSpace(proxy.Host) && !HostNormaliser.TryNormalise(proxy.Host.Trim(), out _, out var error))
        {
            failures.Add(key + ":Host isn't a valid host name or IPv4 address: " + error);
        }
        else if (endpoint is not null && HostNormaliser.TryNormalise(endpoint.Host, out var host, out _)
            && DestinationPolicy.IsDeniedHost(host, endpoint.Port, listenPort, denyLocal: false, out _))
        {
            failures.Add(key + " points back at the relay itself.");
        }
    }
}
