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
Run("formats reset-credit expiration", FormatsResetCreditExpiration, failures);
Run("formats precise reset countdowns without inventing timing", FormatsResetCountdowns, failures);
Run("formats reset dates in the selected time zone", FormatsResetDates, failures);
Run("detects a weekly reset", DetectsWeeklyReset, failures);
Run("detects an unused weekly reset", DetectsUnusedWeeklyReset, failures);
Run("detects a new reset credit", DetectsNewResetCredit, failures);
Run("does not duplicate a weekly-only window", DoesNotDuplicateWeeklyOnlyWindow, failures);
Run("maps tray usage colors", MapsTrayUsageColors, failures);
Run("colors quota by elapsed window pace", ColorsQuotaByPace, failures);
Run("handles missing and expired pace timing", HandlesPaceTiming, failures);
Run("pauses and resumes refresh state", PausesAndResumesRefreshState, failures);
await RunAsync("reuses one app-server process", ReusesOneAppServerProcessAsync, failures);
await RunAsync("reconciles monthly billed costs across pages and models", ReconcilesMonthlyCostsAsync, failures);
await RunAsync("distinguishes an empty report from missing billing access", DistinguishesUnavailableCostsAsync, failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All Codex Tray checks passed.");
return 0;

static async Task ReconcilesMonthlyCostsAsync()
{
    var now = DateTimeOffset.Parse("2026-01-31T22:15:00-05:00");
    var first = """
        {"data":[{"results":[
           {"line_item":"gpt-example, input_tokens","amount":{"value":0.61,"currency":"usd"}},
           {"line_item":"gpt-speech-2025-03-01 text, input","amount":{"value":0.00042,"currency":"usd"}}
        ]}],"has_more":true,"next_page":"part+/2"}
        """;
    var last = """
        {"data":[{"results":[
           {"line_item":"gpt-example, output_tokens","amount":{"value":0.72,"currency":"usd"}},
           {"line_item":"gpt-speech-2025-03-01 audio, output","amount":{"value":0.00031,"currency":"usd"}},
           {"line_item":null,"amount":{"value":-0.20,"currency":"usd"}},
           {"line_item":"Storage","amount":{"value":0.05,"currency":"usd"}}
        ]}],"has_more":false,"next_page":null}
        """;
    using var http = new HttpClient(new CostsHandler(request =>
    {
        var uri = request.RequestUri!;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Equal(HttpMethod.Get, request.Method, "read-only billing request");
        Equal("https://api.openai.com/v1/organization/costs", uri.GetLeftPart(UriPartial.Path), "billing endpoint");
        Equal(DateTimeOffset.Parse("2026-02-01T00:00:00Z").ToUnixTimeSeconds().ToString(), query["start_time"], "UTC month boundary");
        Equal(DateTimeOffset.Parse("2026-02-02T00:00:00Z").ToUnixTimeSeconds().ToString(), query["end_time"], "include the in-progress UTC day");
        Equal("line_item", query["group_by"], "model-bearing cost lines");
        var body = query["page"] switch { null => first, "part+/2" => last, _ => throw new Exception("Invalid page") };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
    }));
    var report = await new OrganizationCostsClient(http).FetchAsync("test", now, default);
    Equal(1.18073m, report.Total, "includes service charges, credits and later pages");
    Equal(new SpendRow("gpt-example", 1.33m), report.Rows[0], "combined model charges sorted first");
    Equal("$0.00073", MonthlySpend.Dollars(report.Rows.Single(r => r.Name == "gpt-speech").Amount), "sub-cent spend");
    Equal(-0.20m, report.Rows.Single(r => r.Name == "Other charges").Amount, "unattributed credit retained");
    True(!report.IsCurrentMonth(now.AddMonths(1)), "old cache excluded after UTC month rollover");
}

static async Task DistinguishesUnavailableCostsAsync()
{
    using var http = new HttpClient(new CostsHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"data":[{"results":[]}],"has_more":false}""")
    }));
    var empty = await new OrganizationCostsClient(http).FetchAsync("test", DateTimeOffset.UtcNow, default);
    Equal(0m, empty.Total, "empty report is zero");
    using var denied = new HttpClient(new CostsHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)));
    try
    {
        await new OrganizationCostsClient(denied).FetchAsync("test", DateTimeOffset.UtcNow, default);
        throw new Exception("Denied access produced a spend total");
    }
    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden) { }
}

static void ParsesRateWindows()
{
    var snapshot = ParseSnapshot();
    Equal(25d, snapshot.SessionWindow?.UsedPercent, "session used percent");
    Equal(75d, snapshot.SessionWindow?.RemainingPercent, "session remaining percent");
    Equal(10_080, snapshot.WeeklyWindow?.WindowMinutes, "weekly duration");
    Equal(snapshot.WeeklyWindow, snapshot.DisplayWindow, "tray prefers weekly quota");
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

static void FormatsResetCountdowns()
{
    var now = DateTimeOffset.Parse("2026-10-22T20:54:00Z");
    Equal("6d 23h 59m", DisplayFormatter.ResetCountdown(now.AddDays(7).AddMinutes(-1), now), "day/hour/minute countdown");
    Equal("23h 59m", DisplayFormatter.ResetCountdown(now.AddDays(1).AddMinutes(-1), now), "sub-day countdown");
    Equal("59m", DisplayFormatter.ResetCountdown(now.AddMinutes(59), now), "sub-hour countdown");
    Equal("Less than 1m", DisplayFormatter.ResetCountdown(now.AddSeconds(59), now), "sub-minute countdown");
    Equal("Refresh needed", DisplayFormatter.ResetCountdown(now, now), "elapsed sample needs refresh");
    Equal("Refresh needed", DisplayFormatter.ResetCountdown(now.AddMinutes(-1), now), "expired sample needs refresh");
    Equal("Not reported", DisplayFormatter.ResetCountdown(null, now), "missing timing stays unknown");
}

static void FormatsResetDates()
{
    var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    try
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        Equal("Thu, Oct 29, 2026 · 4:54 PM ET",
            DisplayFormatter.ResetDate(DateTimeOffset.Parse("2026-10-29T20:54:00Z"), eastern), "Eastern daylight time");
        Equal("Thu, Nov 5, 2026 · 3:54 PM ET",
            DisplayFormatter.ResetDate(DateTimeOffset.Parse("2026-11-05T20:54:00Z"), eastern), "Eastern standard time");
        Equal("Thu, Oct 29, 2026 · 8:54 PM UTC+00:00",
            DisplayFormatter.ResetDate(DateTimeOffset.Parse("2026-10-29T20:54:00Z"), TimeZoneInfo.Utc), "other zone offset");
        Equal("Date not reported", DisplayFormatter.ResetDate(null, eastern), "unknown date");
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentCulture = previousCulture;
    }
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
    Equal(snapshot.WeeklyWindow, snapshot.DisplayWindow, "weekly-only display quota");
}

static void MapsTrayUsageColors()
{
    Equal(UsageBand.Unknown, UsageBandSelector.Select(null), "unknown usage band");
    Equal(UsageBand.Green, UsageBandSelector.Select(25.01), "green usage band");
    Equal(UsageBand.Amber, UsageBandSelector.Select(25), "amber usage band");
    Equal(UsageBand.Amber, UsageBandSelector.Select(15.01), "low amber usage band");
    Equal(UsageBand.Red, UsageBandSelector.Select(15), "red usage band");
    Equal(UsageBand.Red, UsageBandSelector.Select(0), "exhausted quota");
}

static void ColorsQuotaByPace()
{
    var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    var week = new LimitWindow(40, 10080, start.AddDays(7));
    Equal(UsageBand.Red, UsageBandSelector.SelectWindow(week, start.AddDays(1)), "40 percent on day one is red");
    Equal(UsageBand.Green, UsageBandSelector.SelectWindow(week, start.AddDays(3)), "pace catches up without extra usage");
    Equal(UsageBand.Green, UsageBandSelector.SelectWindow(week with { UsedPercent = 55 }, start.AddDays(3.5)), "five-point tolerance boundary");
    Equal(UsageBand.Amber, UsageBandSelector.SelectWindow(week with { UsedPercent = 55.01 }, start.AddDays(3.5)), "just over tolerance");
    Equal(UsageBand.Amber, UsageBandSelector.SelectWindow(week with { UsedPercent = 64.99 }, start.AddDays(3.5)), "below red pace boundary");
    Equal(UsageBand.Red, UsageBandSelector.SelectWindow(week with { UsedPercent = 65 }, start.AddDays(3.5)), "15-point red boundary");
    Equal(UsageBand.Amber, UsageBandSelector.SelectWindow(week with { UsedPercent = 75 }, start.AddDays(6)), "low remaining overrides good pace");
    Equal(UsageBand.Red, UsageBandSelector.SelectWindow(week with { UsedPercent = 85 }, start.AddDays(6.9)), "red reserve overrides good pace");
    Equal(UsageBand.Green, UsageBandSelector.SelectWindow(week with { UsedPercent = 0 }, start), "fresh quota");
    Equal(UsageBand.Red, UsageBandSelector.SelectWindow(new LimitWindow(65, 300, start.AddMinutes(300)), start.AddMinutes(150)), "uses reported duration");
    True(DisplayFormatter.PaceLine(week, start.AddDays(1)).Contains("too fast"), "hover explains fast usage");
    True(DisplayFormatter.PaceLine(week, start.AddDays(3)).Contains("on track"), "hover explains recovered pace");
}

static void HandlesPaceTiming()
{
    var now = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    var week = new LimitWindow(40, 10080, null);
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(null, now), "no data");
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(week, now), "missing reset cannot imply green");
    Equal(UsageBand.Red, UsageBandSelector.SelectWindow(week with { UsedPercent = 90 }, now), "missing timing preserves low reserve warning");
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(week with { ResetsAt = now }, now), "expired sample");
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(week with { ResetsAt = now.AddDays(8) }, now), "reset beyond window");
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(week with { WindowMinutes = 0, ResetsAt = now.AddDays(7) }, now), "missing duration");
    Equal(UsageBand.Unknown, UsageBandSelector.SelectWindow(week with { UsedPercent = double.NaN }, now), "invalid usage");
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

sealed class CostsHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(respond(request));
}
