using AutopilotQuant.Core.Replay;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length >= 2 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
{
    string? reportJsonPath = null;
    if (args.Length == 4
        && string.Equals(args[2], "--report-json", StringComparison.OrdinalIgnoreCase))
    {
        reportJsonPath = args[3];
    }
    else if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: replay <bars.csv> [--report-json <report.json>]");
        return 2;
    }

    var bars = await HistoricalBarCsvReader.ReadFileAsync(args[1]);
    var report = await new HistoricalReplayRunner().RunAsync(bars);

    Console.WriteLine("Autopilot Quant - Historical Paper Replay");
    Console.WriteLine("=========================================");
    Console.WriteLine($"Bars processed: {report.BarsProcessed}");
    Console.WriteLine($"Signal candidates: {report.SignalCandidates}");
    Console.WriteLine($"Paper orders placed: {report.OrdersPlaced}");
    Console.WriteLine($"Risk/position-limit rejections: {report.RiskRejected}");
    Console.WriteLine($"Other order rejections: {report.OtherOrderRejections}");
    Console.WriteLine($"Closed trades: {report.TradesClosed}");
    Console.WriteLine($"Winning / losing trades: {report.WinningTrades} / {report.LosingTrades}");
    Console.WriteLine($"Win rate: {report.WinRate:P2}");
    Console.WriteLine($"Profit factor: {report.ProfitFactor?.ToString("F2") ?? "N/A (no losing trades)"}");
    Console.WriteLine($"Gross profit / loss: {report.GrossProfit:F2} / {report.GrossLoss:F2}");
    Console.WriteLine($"Net P/L after fees: {report.NetProfitLoss:F2}");
    Console.WriteLine($"Ending equity: {report.EndingEquity:F2}");
    Console.WriteLine($"Trading timezone: {report.TimeZoneId}");
    if (reportJsonPath is not null)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.Strict
        });
        await File.WriteAllTextAsync(reportJsonPath, json);
        Console.WriteLine($"JSON report: {Path.GetFullPath(reportJsonPath)}");
    }
    Console.WriteLine();
    Console.WriteLine("Paper simulation only. T4 live order routing is not enabled.");
}
else
{
    Console.WriteLine("Autopilot Quant");
    Console.WriteLine("================");
    Console.WriteLine("Mode: PAPER / DRY RUN");
    Console.WriteLine("Initial instruments: MES, MNQ");
    Console.WriteLine("Short exposure: LOCKED");
    Console.WriteLine("India NSE/BSE: LOCKED");
    Console.WriteLine();
    Console.WriteLine("Safety: T4 live order routing is NOT enabled.");
    Console.WriteLine();
    Console.WriteLine("Historical paper replay:");
    Console.WriteLine("  dotnet run --project src\\AutopilotQuant.Runner -- replay <bars.csv> [--report-json <report.json>]");
    Console.WriteLine("CSV columns: Symbol,Timestamp,Open,High,Low,Close,Volume");
}

return 0;
