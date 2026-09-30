namespace CodexUsageHub.Models;

public sealed class AppSettings
{
    public string? CodexExecutablePath { get; set; }
    public int RefreshSeconds { get; set; } = 60;
    public bool StartMinimized { get; set; }
    public bool OverlayEnabled { get; set; }
    public int OverlayX { get; set; } = 20;
    public int OverlayY { get; set; } = 20;
    public List<CodexAccountProfile> Accounts { get; set; } = [];
}
