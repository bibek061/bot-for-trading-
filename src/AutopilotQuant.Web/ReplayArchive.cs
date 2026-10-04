using System.Text.Json;
using AutopilotQuant.Core.Forward;
using AutopilotQuant.Core.MarketData;
using AutopilotQuant.Core.Replay;

namespace AutopilotQuant.Web;

public sealed record SavedReplay(Guid Id, string Name, DateTimeOffset CreatedAt, string Assumptions,
    HistoricalReplayReport Report);

public sealed class ReplayArchive
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ReplayArchive(string directory)
    {
        _directory = Path.Combine(directory, "replays");
        Directory.CreateDirectory(_directory);
    }
    public object[] List() => Directory.EnumerateFiles(_directory, "*.json")
        .OrderByDescending(File.GetLastWriteTimeUtc).Take(100)
        .Select(file => JsonSerializer.Deserialize<SavedReplay>(File.ReadAllBytes(file), PaperSessionStore.Json)!)
        .Select(r => (object)new { r.Id, r.Name, r.CreatedAt, r.Report.BarsProcessed, r.Report.TradesClosed,
            r.Report.NetProfitLoss, r.Report.WinRate, r.Report.ProfitFactor }).ToArray();
    public SavedReplay? Read(Guid id)
    {
        var path = Path.Combine(_directory, id.ToString("N") + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SavedReplay>(File.ReadAllBytes(path), PaperSessionStore.Json) : null;
    }
    public async Task<SavedReplay> RunAsync(string name, IReadOnlyList<MarketBar> bars, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("A replay is already running.");
        try
        {
            if (Directory.EnumerateFiles(_directory, "*.json").Count() >= 100)
                throw new InvalidOperationException("Replay archive is full (100 runs). Archive older reports locally first.");
            foreach (var bar in bars)
            {
                if (bar.Symbol is not ("MES" or "MNQ") || new[] { bar.Open, bar.High, bar.Low, bar.Close }
                    .Any(p => p <= 0 || p > 1_000_000 || p % 0.25m != 0))
                    throw new ArgumentException("Replay requires MES/MNQ and positive, tick-aligned prices at most 1,000,000.");
            }
            var report = await new HistoricalReplayRunner().RunAsync(bars, ct);
            var safeName = new string(Uri.UnescapeDataString(name).Where(c => !char.IsControl(c)).Take(100).ToArray());
            var run = new SavedReplay(Guid.NewGuid(), string.IsNullOrWhiteSpace(safeName) ? "CSV replay" : safeName,
                DateTimeOffset.UtcNow,
                "Legacy research model: same-close fills, 1 tick slippage, $1.25 fee per side/contract, $10,000 initial equity; "
                + "calendar-day/end-of-file exits. No spread, queue, partial-fill or intrabar stop model. "
                + "Separate from the forward session. User-supplied data is not independently verified.", report);
            PaperSessionStore.WriteAtomic(Path.Combine(_directory, run.Id.ToString("N") + ".json"), run);
            return run;
        }
        finally { _gate.Release(); }
    }
}
