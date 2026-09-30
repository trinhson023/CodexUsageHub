namespace CodexUsageHub.Models;

public sealed class CodexAccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Codex account";
    public string CodexHome { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PlanType { get; set; }
}
