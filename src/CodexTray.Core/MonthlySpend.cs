using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexTray.Core;

public sealed record SpendRow(string Name, decimal Amount);

public sealed record MonthlySpend(DateTimeOffset AsOf, decimal Total, IReadOnlyList<SpendRow> Rows, string Source)
{
    public bool IsCurrentMonth(DateTimeOffset now) =>
        AsOf.UtcDateTime.Year == now.UtcDateTime.Year && AsOf.UtcDateTime.Month == now.UtcDateTime.Month;

    public static string Dollars(decimal value)
    {
        if (value != 0 && Math.Abs(value) < 0.000001m)
        {
            return value < 0 ? "−<$0.000001" : "<$0.000001";
        }

        var format = value != 0 && Math.Abs(value) < 0.01m ? "$0.00####" : "$#,##0.00";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}

public sealed class OrganizationCostsClient(HttpClient http)
{
    public async Task<MonthlySpend> FetchAsync(string adminKey, DateTimeOffset asOf, CancellationToken token)
    {
        var utc = asOf.ToUniversalTime();
        var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        // The Costs API drops the current daily bucket when end_time falls inside it.
        var end = new DateTimeOffset(utc.UtcDateTime.Date.AddDays(1));
        var url = $"https://api.openai.com/v1/organization/costs?start_time={start.ToUnixTimeSeconds()}"
                  + $"&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d&limit=31&group_by=line_item";

        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var uri = page is null ? url : $"{url}&page={Uri.EscapeDataString(page)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var root = document.RootElement;
            foreach (var bucket in root.GetProperty("data").EnumerateArray())
            {
                foreach (var row in bucket.GetProperty("results").EnumerateArray())
                {
                    var amount = row.GetProperty("amount");
                    if (!string.Equals(amount.GetProperty("currency").GetString(), "usd", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The billing report contains a non-USD amount.");
                    }

                    var item = row.TryGetProperty("line_item", out var field) ? field.GetString() : null;
                    var name = ChargeName(item);
                    totals[name] = totals.GetValueOrDefault(name) + amount.GetProperty("value").GetDecimal();
                }
            }
            if (!root.GetProperty("has_more").GetBoolean())
            {
                break;
            }
            page = root.GetProperty("next_page").GetString();
            if (string.IsNullOrWhiteSpace(page) || !seenPages.Add(page))
            {
                throw new InvalidDataException("The billing report has an invalid page cursor.");
            }
        } while (true);

        var rows = totals.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SpendRow(pair.Key, pair.Value)).ToArray();
        return new MonthlySpend(utc, rows.Sum(row => row.Amount), rows, "Costs API");
    }

    private static string ChargeName(string? lineItem)
    {
        var name = lineItem?.Split(',', 2)[0].Trim();
        if (string.IsNullOrEmpty(name))
        {
            return "Other charges";
        }
        // A single model may have separate audio/text meters and dated snapshots.
        // Keep service names with spaces intact.
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1] is "audio" or "text" or "image" or "video")
        {
            name = parts[0];
        }
        if (!name.Contains(' ') && name.Length > 11 && name[^11] == '-'
            && DateOnly.TryParseExact(name.AsSpan(name.Length - 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            name = name[..^11];
        }
        return name;
    }
}
