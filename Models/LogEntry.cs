namespace TunnlSplitVPN.Models;

public sealed class LogEntry
{
    public string Category { get; set; } = "Service";
    public string Message { get; set; } = string.Empty;
    public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string Level { get; set; } = "Neutral";
}
