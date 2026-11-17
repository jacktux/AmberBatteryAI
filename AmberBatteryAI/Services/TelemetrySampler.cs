namespace AmberBatteryAI.Services;

/// <summary>
/// Reads the live telemetry every few minutes (default 5) and stores it, so the daily P&L is built from
/// real power and price samples rather than from the 30-minute decisions alone. It only records; it never writes
/// to Home Assistant. Set Telemetry:SampleIntervalMinutes to 0 to turn it off.
/// </summary>
public class TelemetrySampler(
    TelemetryReader reader,
    TelemetryHistoryService samples,
    IConfiguration config,
    ILogger<TelemetrySampler> logger) : BackgroundService
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int minutes = config.GetValue("Telemetry:SampleIntervalMinutes", 5);
        if (minutes <= 0)
        {
            logger.LogInformation("Telemetry sampling is off (Telemetry:SampleIntervalMinutes = 0).");
            return;
        }

        int pnlDays = config.GetValue("Telemetry:ReportDays", 14);
        var interval = TimeSpan.FromMinutes(Math.Min(minutes, 60));
        var lastReport = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var http = reader.CreateClient();
                samples.Append(await reader.ReadSampleAsync(http, stoppingToken));

                if (DateTimeOffset.Now - lastReport >= ReportEvery)
                {
                    lastReport = DateTimeOffset.Now;
                    var (errors, bias) = samples.BuildForecastBias(pnlDays);
                    samples.WriteReports(samples.BuildDailyPnl(pnlDays), errors, bias);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Telemetry sample failed.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
