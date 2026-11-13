using WSLProxy.Relay.Configuration;
using WSLProxy.Relay.Routing;

namespace WSLProxy.Relay.Tests;

/// <summary>Host normalisation, the bypass list grammar and routing decisions, including the deny list.</summary>
[TestClass]
public sealed class RoutingTests
{
    [TestMethod]
    [DataRow("Example.COM.", "example.com", HostKind.Name)]
    [DataRow("my_host.corp", "my_host.corp", HostKind.Name)]
    [DataRow("bücher.example", "xn--bcher-kva.example", HostKind.Name)]
    [DataRow("10.1.2.3", "10.1.2.3", HostKind.IPv4)]
    [DataRow("0.0.0.0", "0.0.0.0", HostKind.IPv4)]
    public void Normalise_accepts_names_and_strict_ipv4(string input, string expected, HostKind kind)
    {
        Assert.IsTrue(HostNormaliser.TryNormalise(input, out var host, out var error), error);
        Assert.AreEqual(expected, host.Value);
        Assert.AreEqual(kind, host.Kind);
    }

    [TestMethod]
    [DataRow("127.1")]
    [DataRow("2130706433")]
    [DataRow("0x7f.0.0.1")]
    [DataRow("010.0.0.1")]
    [DataRow("1.2.3.256")]
    [DataRow("example.0x10")]
    [DataRow("::1")]
    [DataRow("[::1]")]
    [DataRow("a..b")]
    [DataRow("-a.example")]
    [DataRow("a b.example")]
    [DataRow("host%25.example")]
    [DataRow("")]
    public void Normalise_rejects_ambiguous_or_unsupported_hosts(string input)
    {
        Assert.IsFalse(HostNormaliser.TryNormalise(input, out _, out _));
    }

    [TestMethod]
    [DataRow("example.com", "example.com", 443, true)]
    [DataRow("example.com", "a.b.example.com", 80, true)]
    [DataRow("example.com", "badexample.com", 80, false)]
    [DataRow(".example.com", "example.com", 80, true)]
    [DataRow("*.example.com", "example.com", 80, false)]
    [DataRow("*.example.com", "a.b.example.com", 80, true)]
    [DataRow("Example.COM.", "example.com", 80, true)]
    [DataRow("example.com:8080", "example.com", 8080, true)]
    [DataRow("example.com:8080", "example.com", 80, false)]
    [DataRow("10.1.2.3", "10.1.2.3", 443, true)]
    [DataRow("10.0.0.0/8", "10.200.3.4", 80, true)]
    [DataRow("10.9.9.9/8", "10.0.0.1", 80, true)]
    [DataRow("10.0.0.0/8", "11.0.0.1", 80, false)]
    [DataRow("10.0.0.0/8", "ten.corp", 80, false)]
    [DataRow("example.com", "10.1.2.3", 80, false)]
    [DataRow("*", "anything.example", 1, true)]
    public void NoProxy_rules_match_on_label_boundaries_ports_and_networks(string entry, string host, int port, bool expected)
    {
        Assert.IsTrue(NoProxyRule.TryParse(entry, out var rule, out var error), error);
        Assert.IsTrue(HostNormaliser.TryNormalise(host, out var normalised, out _));
        Assert.AreEqual(expected, rule.Matches(normalised, port));
    }

    [TestMethod]
    [DataRow("10.9.9.9/8", "10.0.0.0/8")]
    [DataRow(".Example.COM.", "example.com")]
    [DataRow("*.Example.com:443", "*.example.com:443")]
    public void NoProxy_rules_normalise_for_display(string entry, string expected)
    {
        Assert.IsTrue(NoProxyRule.TryParse(entry, out var rule, out _));
        Assert.AreEqual(expected, rule.Normalised);
    }

    [TestMethod]
    [DataRow("ex*.com")]
    [DataRow("a.*.b")]
    [DataRow("::1")]
    [DataRow("[::1]:80")]
    [DataRow("10.0.0.0/33")]
    [DataRow("10.0.0.0/8:80")]
    [DataRow("127.1")]
    [DataRow("*.10.1.2.3")]
    [DataRow("example.com:0")]
    [DataRow("example.com:65536")]
    [DataRow("  ")]
    public void NoProxy_rules_reject_unsupported_entries(string entry)
    {
        Assert.IsFalse(NoProxyRule.TryParse(entry, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow("localhost", 80, true, true, RouteKind.Deny)]
    [DataRow("a.localhost", 80, true, true, RouteKind.Deny)]
    [DataRow("127.0.0.1", 80, true, true, RouteKind.Deny)]
    [DataRow("169.254.169.254", 80, true, true, RouteKind.Deny)]
    [DataRow("168.63.129.16", 80, true, true, RouteKind.Deny)]
    [DataRow("224.0.0.1", 80, true, true, RouteKind.Deny)]
    [DataRow("127.0.0.1", 3128, false, true, RouteKind.Deny)]
    [DataRow("localhost", 3128, false, true, RouteKind.Deny)]
    [DataRow("127.0.0.1", 80, false, false, RouteKind.Direct)]
    [DataRow("10.1.2.3", 80, true, true, RouteKind.Direct)]
    [DataRow("192.168.1.10", 443, true, false, RouteKind.Direct)]
    [DataRow("intranet.corp.example", 443, true, true, RouteKind.Direct)]
    [DataRow("corp.example", 443, true, true, RouteKind.Upstream)]
    [DataRow("example.org", 443, true, true, RouteKind.Upstream)]
    [DataRow("example.org", 443, true, false, RouteKind.Direct)]
    public void Router_denies_local_destinations_then_applies_the_bypass_list(string host, int port, bool denyLocal, bool hasUpstream, RouteKind expected)
    {
        var list = NoProxyList.Parse(["10.0.0.0/8", "*.corp.example"], out var errors);
        Assert.IsEmpty(errors);
        var router = new Router(list, hasUpstream, hasUpstream, listenPort: 3128, denyLocal);
        Assert.IsTrue(HostNormaliser.TryNormalise(host, out var normalised, out _));
        Assert.AreEqual(expected, router.Decide(normalised, port, tunnel: false).Kind);
        Assert.AreEqual(expected, router.Decide(normalised, port, tunnel: true).Kind);
    }

    [TestMethod]
    [DataRow("", 80, "", null, null, null)]
    [DataRow(" proxy.example ", 8080, "", null, "proxy.example:8080", "proxy.example:8080")]
    [DataRow("proxy.example", 8080, "", 8443, "proxy.example:8080", "proxy.example:8443")]
    [DataRow("proxy.example", 8080, "secure.example", null, "proxy.example:8080", "secure.example:8080")]
    [DataRow("", 80, "secure.example", 8443, null, "secure.example:8443")]
    [DataRow("proxy.example", null, "", null, "proxy.example:80", "proxy.example:80")]
    public void The_https_proxy_takes_the_http_proxys_host_and_port_when_they_are_empty(
        string httpHost, int? httpPort, string httpsHost, int? httpsPort, string? expectedHttp, string? expectedHttps)
    {
        var upstream = new UpstreamOptions
        {
            Http = new UpstreamProxyOptions { Host = httpHost, Port = httpPort },
            Https = new UpstreamProxyOptions { Host = httpsHost, Port = httpsPort },
        };

        Assert.AreEqual(expectedHttp, upstream.ForHttp?.ToString());
        Assert.AreEqual(expectedHttps, upstream.ForHttps?.ToString());
    }
}
