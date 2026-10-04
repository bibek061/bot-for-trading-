using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AutopilotQuant.Core.MarketData;

namespace AutopilotQuant.T4;

public sealed class T4HistoryClient(HttpClient client)
{
    public async Task<IReadOnlyList<MarketBar>> LoadAsync(T4Market market, string token, string timeZone,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (timeZone is not ("America/Chicago" or "CST")) throw new T4FeedException("Choose the provider's historical timestamp timezone before backfill.", false);
        var zone = Zone(timeZone);
        var localDate = TimeZoneInfo.ConvertTime(now, zone).Date;
        var query = new Dictionary<string, string> {
            ["exchangeId"] = market.ExchangeId, ["contractId"] = market.ProductId, ["marketID"] = market.MarketId,
            ["chartType"] = "Bar", ["barInterval"] = "Minute", ["barPeriod"] = "5", ["resetInterval"] = "None",
            ["tradeDateStart"] = localDate.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["tradeDateEnd"] = localDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        var uri = "https://api-sim.t4login.com/chart/barchart?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new("application/json"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new T4FeedException("T4 historical API rejected the request. Check historical-data access.", false);
        if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new T4FeedException("Historical response exceeded the size limit.", false);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(bytes, deadline.Token)) > 0)
        {
            if (buffer.Length + read > 8 * 1024 * 1024) throw new T4FeedException("Historical response exceeded the size limit.", false);
            buffer.Write(bytes, 0, read);
        }
        return Parse(buffer.ToArray(), market, timeZone, now);
    }

    public static IReadOnlyList<MarketBar> Parse(byte[] json, T4Market market, string timeZone, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var definitions = root.GetProperty("marketDefinitions").EnumerateArray().Where(d => d.GetProperty("marketID").GetString() == market.MarketId).ToArray();
        if (definitions.Length != 1) throw new T4FeedException("Historical contract definition is missing or ambiguous.", false);
        var tick = Decimal(definitions[0].GetProperty("minPriceIncrement"));
        var tickValue = Decimal(definitions[0].GetProperty("tickValue"));
        if (tick <= 0 || tickValue != (market.Symbol == "MES" ? 1.25m : .50m)) throw new T4FeedException("Historical contract economics do not match.", false);
        // Chart JSON uses provider decimal price units. Scale using the returned tick definition.
        var scale = .25m / tick;
        var bars = new List<MarketBar>();
        var seen = new HashSet<DateTimeOffset>();
        foreach (var item in root.GetProperty("bars").EnumerateArray())
        {
            if (item.GetProperty("marketID").GetString() != market.MarketId) throw new T4FeedException("Historical response mixed expiring contracts.", false);
            var start = Timestamp(item.GetProperty("time").GetString()!, timeZone);
            if (start != T4MarketAccumulator.Floor(start)) throw new T4FeedException("Historical five-minute bars are not boundary-aligned.", false);
            var end = start.AddMinutes(5);
            // closeTime can be the final trade's time, not the interval boundary. End is start + five minutes.
            var closeAt = Timestamp(item.GetProperty("closeTime").GetString()!, timeZone);
            if (closeAt < start || closeAt > end) throw new T4FeedException("Historical bar close time is outside its interval.", false);
            if (end > now) continue;
            decimal Price(string name)
            {
                var price = Decimal(item.GetProperty(name)) * scale;
                if (price <= 0 || price > 1_000_000 || price % .25m != 0) throw new T4FeedException("Historical price is invalid.", false);
                return price;
            }
            var bar = new MarketBar(market.Symbol, end, Price("openPrice"), Price("highPrice"), Price("lowPrice"), Price("closePrice"), item.GetProperty("volume").GetInt64());
            if (bar.Volume < 0 || bar.Low > Math.Min(bar.Open, bar.Close) || bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > bar.High || !seen.Add(end))
                throw new T4FeedException("Invalid or duplicate historical bar.", false);
            bars.Add(bar);
            if (bars.Count > 2500) throw new T4FeedException("Historical bar count exceeded the limit.", false);
        }
        return bars.OrderBy(b => b.Timestamp).TakeLast(512).ToArray();
    }
    private static decimal Decimal(JsonElement element) => element.ValueKind == JsonValueKind.Number ? element.GetDecimal()
        : decimal.Parse(element.GetString()!, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    private static TimeZoneInfo Zone(string value) => value switch {
        "CST" => TimeZoneInfo.CreateCustomTimeZone("T4-CST", TimeSpan.FromHours(-6), "T4 CST", "T4 CST"),
        "America/Chicago" => TimeZoneInfo.FindSystemTimeZoneById(value),
        _ => throw new T4FeedException("Historical timezone must be confirmed before backfill.", false)
    };
    public static DateTimeOffset Timestamp(string value, string zoneId)
    {
        if (value.EndsWith('Z') || value.Length > 10 && (value[10..].Contains('+') || value[10..].Contains('-')))
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var local = DateTime.SpecifyKind(DateTime.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        var zone = Zone(zoneId);
        if (zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) throw new T4FeedException("Historical timestamp is ambiguous at a DST transition.", false);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
