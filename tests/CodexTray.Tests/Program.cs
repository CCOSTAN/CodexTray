using System.Text.Json;
using CodexTray.Core;

if (args.Contains("app-server", StringComparer.OrdinalIgnoreCase))
{
    return await RunFakeAppServerAsync();
}

var failures = new List<string>();
Run("parses rate windows and reset credits", ParsesRateWindows, failures);
Run("parses usage summary and today's tokens", ParsesUsage, failures);
Run("prefers the codex bucket", PrefersCodexBucket, failures);
Run("formats bounded tray tooltip", FormatsTooltip, failures);
Run("formats reset-credit expiration", FormatsResetCreditExpiration, failures);
Run("detects a weekly reset", DetectsWeeklyReset, failures);
Run("detects an unused weekly reset", DetectsUnusedWeeklyReset, failures);
Run("detects a new reset credit", DetectsNewResetCredit, failures);
Run("does not duplicate a weekly-only window", DoesNotDuplicateWeeklyOnlyWindow, failures);
Run("maps tray usage colors", MapsTrayUsageColors, failures);
Run("pauses and resumes refresh state", PausesAndResumesRefreshState, failures);
await RunAsync("reuses one app-server process", ReusesOneAppServerProcessAsync, failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All 12 Codex Tray checks passed.");
return 0;

static void ParsesRateWindows()
{
    var snapshot = ParseSnapshot();
    Equal(25d, snapshot.SessionWindow?.UsedPercent, "session used percent");
    Equal(75d, snapshot.SessionWindow?.RemainingPercent, "session remaining percent");
    Equal(10_080, snapshot.WeeklyWindow?.WindowMinutes, "weekly duration");
    Equal(2, snapshot.AvailableResetCredits, "reset-credit count");
    Equal(DateTimeOffset.FromUnixTimeSeconds(1895000000), snapshot.EarliestResetCreditExpiresAt, "reset-credit expiration");
    Equal("pro", snapshot.PlanType, "plan type");
}

static void ParsesUsage()
{
    var snapshot = ParseSnapshot();
    Equal(1_234_567L, snapshot.Usage?.LifetimeTokens, "lifetime tokens");
    Equal(12_345L, snapshot.Usage?.TodayTokens, "today tokens");
}

static void PrefersCodexBucket()
{
    var snapshot = ParseSnapshot();
    Equal("codex", snapshot.PreferredBucket?.Id, "preferred bucket id");
}

static void FormatsTooltip()
{
    var snapshot = ParseSnapshot();
    var tooltip = DisplayFormatter.BuildTooltip(snapshot);
    True(tooltip.StartsWith("Codex: 75% left", StringComparison.Ordinal), "tooltip value");
    True(tooltip.Contains("resets in", StringComparison.Ordinal), "earliest reset countdown");
    Equal(snapshot.SessionWindow?.ResetsAt, snapshot.EarliestResetAt, "earliest reset window");
    True(tooltip.Length <= 63, "tooltip length");
}

static void FormatsResetCreditExpiration()
{
    var snapshot = ParseSnapshot();
    var line = DisplayFormatter.CreditsLine(
        snapshot.AvailableResetCredits,
        snapshot.EarliestResetCreditExpiresAt,
        DateTimeOffset.FromUnixTimeSeconds(1894000000));
    True(line.StartsWith("Reset credits: 2", StringComparison.Ordinal), "reset-credit value");
    True(line.Contains("expires in", StringComparison.Ordinal), "reset-credit expiration countdown");
}

static void DetectsWeeklyReset()
{
    var snapshot = ParseSnapshot();
    var previous = new NotificationState(
        WeeklyUsedPercent: 82,
        WeeklyResetsAt: snapshot.WeeklyWindow!.ResetsAt!.Value.AddDays(-7),
        AvailableResetCredits: snapshot.AvailableResetCredits);
    var notifications = UsageNotificationDetector.Detect(previous, snapshot);
    True(notifications.Any(item => item.Title.Contains("weekly", StringComparison.OrdinalIgnoreCase)), "weekly reset notification");
}

static void DetectsNewResetCredit()
{
    var snapshot = ParseSnapshot();
    var previous = new NotificationState(
        WeeklyUsedPercent: snapshot.WeeklyWindow?.UsedPercent,
        WeeklyResetsAt: snapshot.WeeklyWindow?.ResetsAt,
        AvailableResetCredits: 1);
    var notifications = UsageNotificationDetector.Detect(previous, snapshot);
    True(notifications.Any(item => item.Title.Contains("credit", StringComparison.OrdinalIgnoreCase)), "reset-credit notification");
}

static void DetectsUnusedWeeklyReset()
{
    var snapshot = ParseSnapshot();
    var previous = new NotificationState(
        WeeklyUsedPercent: 0,
        WeeklyResetsAt: snapshot.WeeklyWindow!.ResetsAt!.Value.AddDays(-7),
        AvailableResetCredits: snapshot.AvailableResetCredits);
    var notifications = UsageNotificationDetector.Detect(previous, snapshot);
    True(notifications.Any(item => item.Title.Contains("weekly", StringComparison.OrdinalIgnoreCase)), "unused weekly reset notification");
}

static void DoesNotDuplicateWeeklyOnlyWindow()
{
    using var limits = JsonDocument.Parse("""
        {"id":3,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":11,"windowDurationMins":10080,"resetsAt":1894060800}}}}
        """);
    var snapshot = CodexSnapshotParser.Parse(null, limits.RootElement, null, DateTimeOffset.Now);
    Equal<LimitWindow?>(null, snapshot.SessionWindow, "weekly-only session window");
    Equal(10_080, snapshot.WeeklyWindow?.WindowMinutes, "weekly-only weekly duration");
    True(DisplayFormatter.BuildTooltip(snapshot).StartsWith("Codex: week 89%", StringComparison.Ordinal), "weekly-only tooltip");
}

static void MapsTrayUsageColors()
{
    Equal(UsageBand.Unknown, UsageBandSelector.Select(null), "unknown usage band");
    Equal(UsageBand.Green, UsageBandSelector.Select(51), "green usage band");
    Equal(UsageBand.Amber, UsageBandSelector.Select(50), "amber usage band");
    Equal(UsageBand.Amber, UsageBandSelector.Select(21), "low amber usage band");
    Equal(UsageBand.Red, UsageBandSelector.Select(20), "red usage band");
}

static void PausesAndResumesRefreshState()
{
    var gate = new SessionRefreshGate();
    True(!gate.IsPaused, "refresh starts enabled");
    True(gate.Pause(), "first pause changes state");
    True(gate.IsPaused, "refresh is paused");
    True(!gate.Pause(), "second pause does not change state");
    True(gate.Resume(), "resume changes state");
    True(!gate.IsPaused, "refresh resumes");
}

static async Task ReusesOneAppServerProcessAsync()
{
    var markerPath = Path.Combine(Path.GetTempPath(), $"codex-tray-app-server-{Guid.NewGuid():N}.txt");
    const string markerVariable = "CODEX_TRAY_TEST_APP_SERVER_MARKER";
    var previousMarker = Environment.GetEnvironmentVariable(markerVariable);
    Environment.SetEnvironmentVariable(markerVariable, markerPath);

    try
    {
        using var client = new CodexAppServerClient(
            Environment.ProcessPath ?? throw new InvalidOperationException("Test executable path is unavailable."),
            TimeSpan.FromSeconds(10));
        await client.FetchAsync();
        await client.FetchAsync();

        var starts = await File.ReadAllLinesAsync(markerPath);
        Equal(1, starts.Length, "app-server launch count across two refreshes");
    }
    finally
    {
        Environment.SetEnvironmentVariable(markerVariable, previousMarker);
        File.Delete(markerPath);
    }
}

static async Task<int> RunFakeAppServerAsync()
{
    var markerPath = Environment.GetEnvironmentVariable("CODEX_TRAY_TEST_APP_SERVER_MARKER");
    if (!string.IsNullOrWhiteSpace(markerPath))
    {
        await File.AppendAllTextAsync(markerPath, "started" + Environment.NewLine);
    }

    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idProperty)
            || !idProperty.TryGetInt32(out var id)
            || !root.TryGetProperty("method", out var methodProperty))
        {
            continue;
        }

        object result = methodProperty.GetString() switch
        {
            "account/read" => new
            {
                account = new { type = "chatgpt", email = "test@example.com", planType = "pro" }
            },
            "account/rateLimits/read" => new
            {
                rateLimits = new
                {
                    limitId = "codex",
                    primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = 1893456000 },
                    secondary = new { usedPercent = 40, windowDurationMins = 10080, resetsAt = 1894060800 }
                }
            },
            "account/usage/read" => new
            {
                summary = new { lifetimeTokens = 1234, peakDailyTokens = 123, currentStreakDays = 2 },
                dailyUsageBuckets = Array.Empty<object>()
            },
            _ => new { }
        };

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, result }));
        await Console.Out.FlushAsync();
    }

    return 0;
}

static CodexSnapshot ParseSnapshot()
{
    var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
    using var account = JsonDocument.Parse("""
        {"id":2,"result":{"account":{"type":"chatgpt","email":"user@example.com","planType":"pro"}}}
        """);
    using var limits = JsonDocument.Parse("""
        {"id":3,"result":{
          "rateLimits":{"limitId":"codex","planType":"pro","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1893456000},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1894060800}},
          "rateLimitsByLimitId":{
            "other":{"limitId":"other","primary":{"usedPercent":10,"windowDurationMins":60,"resetsAt":1893456000}},
            "codex":{"limitId":"codex","planType":"pro","primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1893456000},"secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1894060800}}
          },
          "rateLimitResetCredits":{"availableCount":2,"credits":[
            {"id":"credit-1","status":"available","expiresAt":1895000000},
            {"id":"credit-2","status":"available","expiresAt":null}
          ]}
        }}
        """);
    using var usage = JsonDocument.Parse(
        "{\"id\":4,\"result\":{\"summary\":{\"lifetimeTokens\":1234567,\"peakDailyTokens\":45678,\"currentStreakDays\":8},"
        + "\"dailyUsageBuckets\":[{\"startDate\":\"" + today + "\",\"tokens\":12345}]}}");
    return CodexSnapshotParser.Parse(account.RootElement, limits.RootElement, usage.RootElement, DateTimeOffset.Now);
}

static void Run(string name, Action action, List<string> failures)
{
    try
    {
        action();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.Message}");
    }
}

static async Task RunAsync(string name, Func<Task> action, List<string> failures)
{
    try
    {
        await action();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.Message}");
    }
}

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException(label);
    }
}
