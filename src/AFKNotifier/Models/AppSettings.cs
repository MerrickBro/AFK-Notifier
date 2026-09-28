namespace AFKNotifier.Models;

public sealed class AppSettings
{
    public string? ProcessName { get; set; }
    public string? OutputDeviceId { get; set; }
    public string TriggerPhrase { get; set; } = "AFK";
    public double BeepIntervalSeconds { get; set; } = 1.0;
    public double IdleBeepVolumePercent { get; set; } = 28;
    public double IdleBeepPitchHz { get; set; } = 750;
    public double ConfirmationBeepVolumePercent { get; set; } = 28;
    public double ConfirmationBeepPitchHz { get; set; } = 1000;
}
