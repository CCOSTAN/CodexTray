using System.Text.Json;
using CodexTray.Core;

try
{
    var codexPath = CodexLocator.Locate();
    var client = new CodexAppServerClient(codexPath);
    var snapshot = await client.FetchAsync();
    var safeSummary = new
    {
        ok = true,
        codexExecutable = Path.GetFileName(codexPath),
        snapshot.PlanType,
        sessionUsedPercent = snapshot.SessionWindow?.UsedPercent,
        sessionRemainingPercent = snapshot.SessionWindow?.RemainingPercent,
        sessionResetsAt = snapshot.SessionWindow?.ResetsAt,
        weeklyUsedPercent = snapshot.WeeklyWindow?.UsedPercent,
        weeklyRemainingPercent = snapshot.WeeklyWindow?.RemainingPercent,
        weeklyResetsAt = snapshot.WeeklyWindow?.ResetsAt,
        snapshot.AvailableResetCredits,
        earliestResetCreditExpiresAt = snapshot.EarliestResetCreditExpiresAt,
        todayTokens = snapshot.Usage?.TodayTokens,
        lifetimeTokens = snapshot.Usage?.LifetimeTokens,
        bucketCount = snapshot.Buckets.Count,
        buckets = snapshot.Buckets.Select(bucket => new
        {
            bucket.Id,
            primaryMinutes = bucket.Primary?.WindowMinutes,
            secondaryMinutes = bucket.Secondary?.WindowMinutes
        })
    };
    Console.WriteLine(JsonSerializer.Serialize(safeSummary, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Probe failed: {ex.Message}");
    return 1;
}
