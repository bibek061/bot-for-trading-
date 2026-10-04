using AutopilotQuant.Core.Forward;

namespace AutopilotQuant.Web;

public sealed class SessionMonitor(PaperSession session, ILogger<SessionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var faultReported = false;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await session.MonitorAsync(); }
                catch (Exception ex)
                {
                    if (!faultReported) logger.LogError(ex, "Paper monitor fault; inspect before resuming");
                    faultReported = true;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
