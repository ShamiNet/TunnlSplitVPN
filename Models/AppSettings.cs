namespace TunnlSplitVPN.Models;

public sealed class AppSettings
{
    public string? SelectedTunnel { get; set; }
    public string WireSockPath { get; set; } = string.Empty;
    public Dictionary<string, TunnelRules> TunnelRules { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
