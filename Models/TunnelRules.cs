namespace TunnlSplitVPN.Models;

public sealed class TunnelRules
{
    public List<string> IncludeApps { get; set; } = [];
    public List<string> ExcludeApps { get; set; } = [];
    public bool UseTunnelDns { get; set; } = true;
    public string CustomDns { get; set; } = string.Empty;
}
