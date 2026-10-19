namespace TunnlSplitVPN.Models;

public sealed class TunnelProfile
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required string ConfigText { get; init; }
    public required string Addresses { get; init; }
}
