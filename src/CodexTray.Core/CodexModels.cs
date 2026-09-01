namespace CodexTray.Core;

public sealed record LimitWindow(
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

public sealed record CreditBalance(bool HasCredits, bool Unlimited, string? Balance);

public sealed record ResetCredit(DateTimeOffset? ExpiresAt);

public sealed record LimitBucket(
    string Id,
    string DisplayName,
    string? PlanType,
    LimitWindow? Primary,
    LimitWindow? Secondary,
    CreditBalance? Credits,
    string? LimitReachedType);

public sealed record UsageSummary(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    long? LongestRunningTurnSeconds,
    int? CurrentStreakDays,
    int? LongestStreakDays,
    long? TodayTokens);

public sealed record CodexSnapshot(
    DateTimeOffset FetchedAt,
    string? AccountDisplay,
    string? PlanType,
    IReadOnlyList<LimitBucket> Buckets,
    int? AvailableResetCredits,
    IReadOnlyList<ResetCredit> ResetCredits,
    UsageSummary? Usage)
{
    public LimitBucket? PreferredBucket =>
        Buckets.FirstOrDefault(bucket => bucket.Id.Equals("codex", StringComparison.OrdinalIgnoreCase))
        ?? Buckets.FirstOrDefault();

    public LimitWindow? SessionWindow
    {
        get
        {
            var windows = PreferredWindows();
            return windows.FirstOrDefault(window => window.WindowMinutes > 0 && window.WindowMinutes <= 24 * 60)
                   ?? (windows.Count > 1
                       ? windows.OrderBy(window => window.WindowMinutes).FirstOrDefault()
                       : null);
        }
    }

    public LimitWindow? DisplayWindow => SessionWindow ?? WeeklyWindow;

    public DateTimeOffset? EarliestResetAt
    {
        get
        {
            var resets = PreferredWindows()
                .Select(window => window.ResetsAt)
                .OfType<DateTimeOffset>()
                .ToArray();
            return resets.Length == 0 ? null : resets.Min();
        }
    }

    public DateTimeOffset? EarliestResetCreditExpiresAt
    {
        get
        {
            var expirations = ResetCredits
                .Select(credit => credit.ExpiresAt)
                .OfType<DateTimeOffset>()
                .ToArray();
            return expirations.Length == 0 ? null : expirations.Min();
        }
    }

    public LimitWindow? WeeklyWindow
    {
        get
        {
            var windows = PreferredWindows();
            return windows.FirstOrDefault(window => window.WindowMinutes >= 6 * 24 * 60)
                   ?? (windows.Count > 1
                       ? windows.OrderByDescending(window => window.WindowMinutes).FirstOrDefault()
                       : null);
        }
    }

    private List<LimitWindow> PreferredWindows()
    {
        var bucket = PreferredBucket;
        if (bucket is null)
        {
            return [];
        }

        return new[] { bucket.Primary, bucket.Secondary }
            .OfType<LimitWindow>()
            .ToList();
    }
}
