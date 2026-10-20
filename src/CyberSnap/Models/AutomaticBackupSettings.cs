namespace CyberSnap.Models;

public enum BackupFrequency { OnStartup, Daily, Weekly, Monthly }

public sealed class AutomaticBackupSettings
{
    public bool Enabled { get; set; } = true;
    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;
    public int MaxBackups { get; set; } = 3;
    public string CustomDirectory { get; set; } = "";

    public AutomaticBackupSettings Normalize() => new()
    {
        Enabled = Enabled,
        Frequency = Enum.IsDefined(Frequency) ? Frequency : BackupFrequency.Daily,
        MaxBackups = Math.Clamp(MaxBackups, 1, 50),
        CustomDirectory = CustomDirectory?.Trim() ?? ""
    };
}
