using System.Globalization;
using System.Text.Json;

namespace CodexTray.Core;

public static class CodexSnapshotParser
{
    public static CodexSnapshot Parse(
        JsonElement? accountResponse,
        JsonElement rateLimitsResponse,
        JsonElement? usageResponse,
        DateTimeOffset fetchedAt)
    {
        var rateResult = RequiredResult(rateLimitsResponse, "rate limits");
        var buckets = ParseBuckets(rateResult);
        if (buckets.Count == 0)
        {
            throw new InvalidOperationException("Codex returned no rate-limit buckets.");
        }

        var account = OptionalResult(accountResponse);
        var accountObject = account is { } accountResult
            && accountResult.TryGetProperty("account", out var parsedAccount)
            && parsedAccount.ValueKind == JsonValueKind.Object
                ? parsedAccount
                : (JsonElement?)null;

        var accountDisplay = GetString(accountObject, "email")
                             ?? GetString(accountObject, "name");
        var planType = GetString(accountObject, "planType")
                       ?? buckets.Select(bucket => bucket.PlanType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        JsonElement? resetCreditSummary = rateResult.TryGetProperty("rateLimitResetCredits", out var credits)
                                           && credits.ValueKind == JsonValueKind.Object
            ? credits
            : null;
        var resetCredits = GetInt(resetCreditSummary, "availableCount");
        var resetCreditDetails = ParseResetCredits(resetCreditSummary);

        return new CodexSnapshot(
            fetchedAt,
            accountDisplay,
            planType,
            buckets,
            resetCredits,
            resetCreditDetails,
            ParseUsage(OptionalResult(usageResponse), fetchedAt));
    }

    private static List<LimitBucket> ParseBuckets(JsonElement result)
    {
        var buckets = new List<LimitBucket>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var byId)
            && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    buckets.Add(ParseBucket(property.Value, property.Name));
                }
            }
        }

        if (buckets.Count == 0
            && result.TryGetProperty("rateLimits", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object)
        {
            buckets.Add(ParseBucket(legacy, "codex"));
        }

        return buckets;
    }

    private static LimitBucket ParseBucket(JsonElement value, string fallbackId)
    {
        var id = GetString(value, "limitId") ?? fallbackId;
        var name = GetString(value, "limitName");
        return new LimitBucket(
            id,
            string.IsNullOrWhiteSpace(name) ? FriendlyBucketName(id) : name,
            GetString(value, "planType"),
            ParseWindow(value, "primary"),
            ParseWindow(value, "secondary"),
            ParseCredits(value),
            GetString(value, "rateLimitReachedType"));
    }

    private static LimitWindow? ParseWindow(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var used = GetDouble(window, "usedPercent");
        var duration = GetInt(window, "windowDurationMins") ?? 0;
        var resetUnix = GetLong(window, "resetsAt");
        DateTimeOffset? resetsAt = resetUnix is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

        return new LimitWindow(Math.Clamp(used ?? 0d, 0d, 100d), duration, resetsAt);
    }

    private static CreditBalance? ParseCredits(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("credits", out var credits)
            || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CreditBalance(
            GetBool(credits, "hasCredits") ?? false,
            GetBool(credits, "unlimited") ?? false,
            GetString(credits, "balance"));
    }

    private static IReadOnlyList<ResetCredit> ParseResetCredits(JsonElement? parent)
    {
        if (parent is not { } value
            || !value.TryGetProperty("credits", out var credits)
            || credits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<ResetCredit>();
        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var status = GetString(credit, "status");
            if (status is not null && !status.Equals("available", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expiresUnix = GetLong(credit, "expiresAt");
            parsed.Add(new ResetCredit(
                expiresUnix is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : null));
        }

        return parsed;
    }

    private static UsageSummary? ParseUsage(JsonElement? result, DateTimeOffset fetchedAt)
    {
        if (result is not { } usageResult)
        {
            return null;
        }

        JsonElement? summary = usageResult.TryGetProperty("summary", out var parsedSummary)
                               && parsedSummary.ValueKind == JsonValueKind.Object
            ? parsedSummary
            : null;

        long? todayTokens = null;
        if (usageResult.TryGetProperty("dailyUsageBuckets", out var daily)
            && daily.ValueKind == JsonValueKind.Array)
        {
            var localToday = DateOnly.FromDateTime(fetchedAt.LocalDateTime);
            foreach (var bucket in daily.EnumerateArray())
            {
                var startDate = GetString(bucket, "startDate");
                if (DateOnly.TryParse(startDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                    && parsedDate == localToday)
                {
                    todayTokens = GetLong(bucket, "tokens");
                    break;
                }
            }
        }

        if (summary is null && todayTokens is null)
        {
            return null;
        }

        return new UsageSummary(
            GetLong(summary, "lifetimeTokens"),
            GetLong(summary, "peakDailyTokens"),
            GetLong(summary, "longestRunningTurnSec"),
            GetInt(summary, "currentStreakDays"),
            GetInt(summary, "longestStreakDays"),
            todayTokens);
    }

    private static JsonElement RequiredResult(JsonElement response, string label)
    {
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"Codex app-server returned an error for {label}: {Compact(error)}");
        }

        if (!response.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Codex app-server returned no result for {label}.");
        }

        return result;
    }

    private static JsonElement? OptionalResult(JsonElement? response)
    {
        if (response is not { } value
            || !value.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return result;
    }

    private static string FriendlyBucketName(string id) =>
        id.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "Codex"
            : id.Replace('_', ' ');

    private static string Compact(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 180 ? text : text[..180] + "...";
    }

    private static string? GetString(JsonElement? parent, string name)
    {
        if (parent is not { } value
            || !value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? GetInt(JsonElement? parent, string name)
    {
        if (parent is not { } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.TryGetInt32(out var result) ? result : null;
    }

    private static long? GetLong(JsonElement? parent, string name)
    {
        if (parent is not { } value || !value.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static double? GetDouble(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) && property.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static bool? GetBool(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;
    }
}
