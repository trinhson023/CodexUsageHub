namespace CodexUsageHub.Models;

public sealed class UsageSnapshot
{
    public required string ProfileId { get; init; }
    public string? Email { get; init; }
    public string? PlanType { get; init; }
    public string? AccountId { get; init; }
    public RateLimitWindowSnapshot? Primary { get; init; }
    public RateLimitWindowSnapshot? Secondary { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string? Error { get; init; }

    public bool IsSuccess => string.IsNullOrWhiteSpace(Error);
}

public sealed class RateLimitWindowSnapshot
{
    public int UsedPercent { get; init; }
    public long? WindowDurationMins { get; init; }
    public long? ResetsAtUnixSeconds { get; init; }

    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    public DateTimeOffset? ResetsAt => ResetsAtUnixSeconds is long unix
        ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime()
        : null;
}
