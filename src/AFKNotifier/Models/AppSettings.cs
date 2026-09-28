namespace AFKNotifier.Models;

public sealed class AppSettings
{
    public string? ProcessName { get; set; }
    public string? OutputDeviceId { get; set; }
    public string TriggerPhrase { get; set; } = "AFK";
    public double BeepIntervalSeconds { get; set; } = 1.0;
}
