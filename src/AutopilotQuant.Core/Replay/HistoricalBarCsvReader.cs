using System.Globalization;
using System.Text.RegularExpressions;
using AutopilotQuant.Core.MarketData;

namespace AutopilotQuant.Core.Replay;

public static class HistoricalBarCsvReader
{
    private static readonly Regex ExplicitTimeZone = new(
        @"(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] RequiredColumns =
        ["Symbol", "Timestamp", "Open", "High", "Low", "Close", "Volume"];

    public static async Task<IReadOnlyList<MarketBar>> ReadFileAsync(
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path);
        return await ReadAsync(reader, ct);
    }

    public static async Task<IReadOnlyList<MarketBar>> ReadAsync(
        TextReader reader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var headerLine = await reader.ReadLineAsync(ct)
            ?? throw new InvalidDataException("CSV file is empty.");
        var headers = ParseCsvLine(headerLine);
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            if (!indexes.TryAdd(headers[i].Trim(), i))
                throw new InvalidDataException($"Duplicate CSV column '{headers[i]}'.");
        }

        foreach (var column in RequiredColumns)
        {
            if (!indexes.ContainsKey(column))
                throw new InvalidDataException($"CSV is missing required column '{column}'.");
        }

        var bars = new List<MarketBar>();
        var lineNumber = 1;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);
            if (fields.Count != headers.Count)
                throw new InvalidDataException($"CSV line {lineNumber} has {fields.Count} fields; expected {headers.Count}.");

            string Get(string column) => fields[indexes[column]].Trim();
            var timestampText = Get("Timestamp");
            if (!ExplicitTimeZone.IsMatch(timestampText))
                throw new InvalidDataException(
                    $"CSV line {lineNumber} Timestamp must include an explicit timezone offset.");
            if (!DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp))
                throw new InvalidDataException($"CSV line {lineNumber} has an invalid Timestamp.");

            bars.Add(new MarketBar(
                Get("Symbol"),
                timestamp,
                ParseDecimal(Get("Open"), "Open", lineNumber),
                ParseDecimal(Get("High"), "High", lineNumber),
                ParseDecimal(Get("Low"), "Low", lineNumber),
                ParseDecimal(Get("Close"), "Close", lineNumber),
                ParseVolume(Get("Volume"), lineNumber)));
        }

        if (bars.Count == 0)
            throw new InvalidDataException("CSV contains no market bars.");

        return bars;
    }

    private static decimal ParseDecimal(string value, string column, int lineNumber)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"CSV line {lineNumber} has an invalid {column} value.");
        return result;
    }

    private static long ParseVolume(string value, int lineNumber)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"CSV line {lineNumber} has an invalid Volume value.");
        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
            throw new InvalidDataException("CSV line contains an unterminated quoted field.");
        fields.Add(field.ToString());
        return fields;
    }
}
