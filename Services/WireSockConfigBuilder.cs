using System.Globalization;
using System.IO;
using System.Net;

namespace TunnlSplitVPN.Services;

public static class WireSockConfigBuilder
{
    private sealed record Entry(string Section, string Key, string Value, int PeerNumber);

    public static bool TryValidate(string rawConfig, out string error)
    {
        ArgumentNullException.ThrowIfNull(rawConfig);
        error = string.Empty;
        var entries = Parse(rawConfig);

        var interfaces = entries.Where(entry => entry.Section == "Interface").ToList();
        if (!HasSingleSection(rawConfig, "Interface"))
        {
            error = "The configuration must contain exactly one [Interface] section.";
            return false;
        }

        if (!HasValue(interfaces, "PrivateKey"))
        {
            error = "The [Interface] section is missing PrivateKey.";
            return false;
        }

        if (!IsWireGuardKey(GetValue(interfaces, "PrivateKey")))
        {
            error = "The [Interface] PrivateKey must be a valid 32-byte WireGuard key.";
            return false;
        }

        var addresses = GetValue(interfaces, "Address");
        if (string.IsNullOrWhiteSpace(addresses))
        {
            error = "The [Interface] section is missing Address.";
            return false;
        }

        if (!addresses.Split(',').All(IsCidr))
        {
            error = "Address must contain valid IP addresses with CIDR prefixes.";
            return false;
        }

        if (HasValue(interfaces, "DNS") && !IsValidDnsList(GetValue(interfaces, "DNS")))
        {
            error = "The configured DNS value must contain valid IP addresses or host names.";
            return false;
        }

        var peers = entries.Where(entry => entry.Section == "Peer").ToList();
        if (CountSections(rawConfig, "Peer") == 0)
        {
            error = "The configuration must contain at least one [Peer] section.";
            return false;
        }

        foreach (var peer in peers.GroupBy(entry => entry.PeerNumber))
        {
            if (!HasValue(peer, "PublicKey") || !IsWireGuardKey(GetValue(peer, "PublicKey")))
            {
                error = "Every [Peer] section must contain a valid 32-byte PublicKey.";
                return false;
            }

            if (!HasValue(peer, "Endpoint") || !IsValidEndpoint(GetValue(peer, "Endpoint")))
            {
                error = "Every [Peer] section must contain a valid Endpoint with a port.";
                return false;
            }

            if (!HasValue(peer, "AllowedIPs") ||
                !GetValue(peer, "AllowedIPs").Split(',').All(IsCidr))
            {
                error = "Every [Peer] section must contain valid AllowedIPs networks.";
                return false;
            }

            if (HasValue(peer, "PresharedKey") && !IsWireGuardKey(GetValue(peer, "PresharedKey")))
            {
                error = "A [Peer] PresharedKey is present but is not a valid 32-byte WireGuard key.";
                return false;
            }
        }

        return true;
    }

    public static string Build(
        string rawConfig,
        IEnumerable<string> includeApps,
        IEnumerable<string> excludeApps,
        bool useTunnelDns,
        string customDns)
    {
        ArgumentNullException.ThrowIfNull(rawConfig);
        if (!TryValidate(rawConfig, out var validationError))
            throw new InvalidDataException(validationError);

        var includeList = NormalizeApps(includeApps);
        var excludeList = NormalizeApps(excludeApps);
        if (includeList.Intersect(excludeList, StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("An application cannot be in both Include and Exclude.");

        var dns = customDns.Trim();
        if (useTunnelDns && dns.Length > 0 && !IsValidDnsList(dns))
            throw new InvalidDataException("Custom DNS must contain valid IP addresses or host names.");

        var configHasDns = Parse(rawConfig).Any(entry =>
            entry.Section == "Interface" && entry.Key.Equals("DNS", StringComparison.OrdinalIgnoreCase));
        if (useTunnelDns && (configHasDns || dns.Length > 0) && includeList.Count > 0 &&
            !includeList.Contains("Dnscache", StringComparer.OrdinalIgnoreCase))
        {
            includeList.Add("Dnscache");
        }

        var sourceLines = rawConfig.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new List<string>(sourceLines.Length + 8);
        var currentSection = string.Empty;
        var interfaceHasDns = false;
        var interfaceDnsWritten = false;
        var peerOpen = false;

        void FinishPeer()
        {
            if (!peerOpen)
                return;

            if (includeList.Count > 0)
                result.Add("#@ws:AllowedApps = " + string.Join(", ", includeList));

            if (excludeList.Count > 0)
                result.Add("#@ws:DisallowedApps = " + string.Join(", ", excludeList));

            peerOpen = false;
        }

        foreach (var rawLine in sourceLines)
        {
            var trimmed = rawLine.Trim();

            if (TryReadSection(trimmed, out var section))
            {
                FinishPeer();
                currentSection = section;
                peerOpen = section.Equals("Peer", StringComparison.OrdinalIgnoreCase);
                result.Add(rawLine);
                continue;
            }

            if (currentSection.Equals("Peer", StringComparison.OrdinalIgnoreCase) &&
                IsWireSockAppDirective(trimmed))
                continue;

            if (currentSection.Equals("Interface", StringComparison.OrdinalIgnoreCase) &&
                TryReadKey(trimmed, out var key, out _) &&
                key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
            {
                interfaceHasDns = true;
                if (!useTunnelDns)
                    continue;

                if (dns.Length > 0 && !interfaceDnsWritten)
                {
                    result.Add($"{rawLine[..(rawLine.IndexOf('=') + 1)]} {dns}");
                    interfaceDnsWritten = true;
                }
                else if (dns.Length == 0 && !interfaceDnsWritten)
                {
                    result.Add(rawLine);
                    interfaceDnsWritten = true;
                }

                continue;
            }

            result.Add(rawLine);
        }

        FinishPeer();

        if (useTunnelDns && dns.Length > 0 && !interfaceHasDns)
        {
            var interfaceIndex = result.FindLastIndex(line =>
                line.Trim().Equals("[Interface]", StringComparison.OrdinalIgnoreCase));
            if (interfaceIndex < 0)
                throw new InvalidDataException("The [Interface] section could not be located.");

            var insertAt = result.FindIndex(interfaceIndex + 1, line =>
                TryReadSection(line.Trim(), out _));
            if (insertAt < 0)
                insertAt = result.Count;

            result.Insert(insertAt, $"DNS = {dns}");
        }

        return string.Join(Environment.NewLine, result).TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<Entry> Parse(string config)
    {
        var currentSection = string.Empty;
        var peerNumber = -1;
        foreach (var rawLine in config.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#@ws:", StringComparison.OrdinalIgnoreCase))
                line = line["#@ws:".Length..].Trim();
            else if (line.StartsWith('#') || line.StartsWith(';') || line.Length == 0)
                continue;

            if (TryReadSection(line, out var section))
            {
                currentSection = section;
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                    peerNumber++;
                continue;
            }

            if (TryReadKey(line, out var key, out var value) &&
                (currentSection.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
                 currentSection.Equals("Peer", StringComparison.OrdinalIgnoreCase)))
            {
                var normalizedSection = currentSection.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                    ? "Interface"
                    : "Peer";
                yield return new Entry(normalizedSection, key, value,
                    currentSection.Equals("Peer", StringComparison.OrdinalIgnoreCase) ? peerNumber : -1);
            }
        }
    }

    private static bool HasSingleSection(string config, string section) =>
        CountSections(config, section) == 1;

    private static int CountSections(string config, string section) =>
        config.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Count(line => TryReadSection(line.Trim(), out var parsed) &&
                           parsed.Equals(section, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadSection(string line, out string section)
    {
        section = string.Empty;
        if (!line.StartsWith('[') || !line.EndsWith(']'))
            return false;

        section = line[1..^1].Trim();
        return section.Length > 0;
    }

    private static bool TryReadKey(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0)
            return false;

        key = line[..equalsIndex].Trim();
        value = line[(equalsIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static bool HasValue(IEnumerable<Entry> entries, string key) =>
        !string.IsNullOrWhiteSpace(GetValue(entries, key));

    private static string GetValue(IEnumerable<Entry> entries, string key) =>
        entries.LastOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value
        ?? string.Empty;

    private static bool IsWireGuardKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsCidr(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0].Trim(), out var address) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            return false;

        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefix >= 0 && prefix <= maxPrefix;
    }

    private static bool IsValidEndpoint(string value)
    {
        var endpoint = value.Trim();
        var splitIndex = endpoint.LastIndexOf(':');
        if (splitIndex <= 0 || splitIndex == endpoint.Length - 1 ||
            !int.TryParse(endpoint[(splitIndex + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return false;

        var host = endpoint[..splitIndex].Trim();
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        return IPAddress.TryParse(host, out _) ||
               Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static bool IsValidDnsList(string value)
    {
        var hosts = value.Split(',', StringSplitOptions.TrimEntries);
        return hosts.Length > 0 && hosts.All(host => host.Length > 0 &&
            (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns));
    }

    private static List<string> NormalizeApps(IEnumerable<string> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in apps)
        {
            var app = item.Trim().Trim('"', '\'');
            if (app.Length == 0)
                continue;
            if (app.Contains(',') || app.Contains('\r') || app.Contains('\n'))
                throw new InvalidDataException("Application entries cannot contain commas or line breaks.");

            if (seen.Add(app))
                result.Add(app);
        }

        return result;
    }

    private static bool IsWireSockAppDirective(string line)
    {
        if (!line.StartsWith("#@ws:", StringComparison.OrdinalIgnoreCase))
            return false;

        var directive = line["#@ws:".Length..].Trim();
        return TryReadKey(directive, out var key, out _) &&
               (key.Equals("AllowedApps", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DisallowedApps", StringComparison.OrdinalIgnoreCase));
    }
}
