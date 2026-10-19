using TunnlSplitVPN.Services;
using Xunit;

namespace TunnlSplitVPN.Tests;

public sealed class WireSockConfigBuilderTests
{
    private static readonly string PrivateKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string PublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    private const string ValidConfig =
        "[Interface]\n" +
        "PrivateKey = PRIVATE_KEY\n" +
        "Address = 10.0.0.2/32\n" +
        "DNS = 9.9.9.9\n\n" +
        "[Peer]\n" +
        "PublicKey = PUBLIC_KEY\n" +
        "Endpoint = vpn.example.com:51820\n" +
        "AllowedIPs = 0.0.0.0/0, ::/0\n";

    [Fact]
    public void ValidateAcceptsValidWireGuardConfigWithoutExposingKeys()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey);

        Assert.True(WireSockConfigBuilder.TryValidate(config, out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("PrivateKey = ", "PrivateKey")]
    [InlineData("Address = 10.0.0.2/99", "Address")]
    [InlineData("Endpoint = vpn.example.com:70000", "Endpoint")]
    [InlineData("AllowedIPs = not-a-network", "AllowedIPs")]
    public void ValidateRejectsMissingOrMalformedRequiredValues(string replacement, string expectedError)
    {
        var config = ValidConfig
            .Replace("PRIVATE_KEY", PrivateKey)
            .Replace("PUBLIC_KEY", PublicKey);
        var key = replacement[..replacement.IndexOf('=')].Trim();
        var start = config.IndexOf(key + " = ", StringComparison.Ordinal);
        var end = config.IndexOf('\n', start);
        config = config[..start] + replacement + config[end..];

        Assert.False(WireSockConfigBuilder.TryValidate(config, out var error));
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateKey, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateChecksEveryPeerAndRejectsEmptyDnsEntries()
    {
        var invalidDnsConfig = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey)
            .Replace("DNS = 9.9.9.9", "DNS = 9.9.9.9,,1.1.1.1");
        Assert.False(WireSockConfigBuilder.TryValidate(invalidDnsConfig, out var dnsError));
        Assert.Contains("DNS", dnsError, StringComparison.OrdinalIgnoreCase);

        var missingSecondPeerKey = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey) +
                     "\n[Peer]\n" +
                     "Endpoint = backup.example.com:51820\n" +
                     "AllowedIPs = 10.1.0.0/16\n";

        Assert.False(WireSockConfigBuilder.TryValidate(missingSecondPeerKey, out var peerError));
        Assert.Contains("PublicKey", peerError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAddsWireSockDirectivesToEveryPeerAndUsesCustomDns()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey) +
                     "\n[Peer]\n" +
                     $"PublicKey = {PublicKey}\n" +
                     "Endpoint = backup.example.com:51820\n" +
                     "AllowedIPs = 10.1.0.0/16\n";

        var result = WireSockConfigBuilder.Build(config, ["browser.exe"], ["chat"], true, "1.1.1.1");

        Assert.Equal(2, Count(result, "#@ws:AllowedApps = browser.exe, Dnscache"));
        Assert.Equal(2, Count(result, "#@ws:DisallowedApps = chat"));
        Assert.Contains("DNS = 1.1.1.1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS = 9.9.9.9", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\nAllowedApps =", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\nDisallowedApps =", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreservesTunnelDnsAndDoesNotInventRoutingValues()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey);

        var result = WireSockConfigBuilder.Build(config, [], [], true, "");

        Assert.Contains("DNS = 9.9.9.9", result, StringComparison.Ordinal);
        Assert.Contains("AllowedIPs = 0.0.0.0/0, ::/0", result, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowedApps", result, StringComparison.Ordinal);
        Assert.DoesNotContain("1.1.1.1", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCanDisableTunnelDnsWithoutAddingAppRules()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey);

        var result = WireSockConfigBuilder.Build(config, ["browser"], [], false, "");

        Assert.DoesNotContain("DNS =", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Dnscache", result, StringComparison.Ordinal);
        Assert.Contains("#@ws:AllowedApps = browser", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsAppsThatAreBothIncludedAndExcluded()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey);

        Assert.Throws<InvalidDataException>(() =>
            WireSockConfigBuilder.Build(config, ["browser.exe"], ["browser.exe"], true, ""));
    }

    [Fact]
    public void BuildIgnoresCustomDnsWhenTunnelDnsIsDisabled()
    {
        var config = ValidConfig.Replace("PRIVATE_KEY", PrivateKey).Replace("PUBLIC_KEY", PublicKey);

        var result = WireSockConfigBuilder.Build(config, [], [], false, "not a DNS value");

        Assert.DoesNotContain("DNS =", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeLogLineRedactsWireGuardKeys()
    {
        var line = $"PrivateKey = {PrivateKey}; PresharedKey: {PublicKey}";

        var result = WireSockController.SanitizeLogLine(line);

        Assert.DoesNotContain(PrivateKey, result, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicKey, result, StringComparison.Ordinal);
        Assert.Equal(2, Count(result, "<redacted>"));
    }

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;
}
