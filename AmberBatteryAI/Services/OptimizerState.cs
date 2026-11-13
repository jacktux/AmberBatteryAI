using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

public class OptimizerState
{
    private readonly object _lock = new();
    private CancellationTokenSource _delayCts = new();

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastRunUtc { get; private set; }
    public DateTimeOffset? NextRunUtc { get; private set; }
    public string? LastError { get; private set; }
    public string? TelemetrySummary { get; private set; }
    public string? Prompt { get; private set; }
    public string? GridStatus { get; private set; }
    public AppliedDecision? Decision { get; private set; }

    public void SetRunning(bool running)
    {
        lock (_lock) IsRunning = running;
    }

    public void SetNextRun(DateTimeOffset nextRunUtc)
    {
        lock (_lock) NextRunUtc = nextRunUtc;
    }

    public void SetGridStatus(string gridStatus)
    {
        lock (_lock) GridStatus = gridStatus;
    }

    public void RecordSuccess(
        AppliedDecision decision,
        string telemetrySummary,
        string prompt)
    {
        lock (_lock)
        {
            LastRunUtc = DateTimeOffset.UtcNow;
            LastError = null;
            Decision = decision;
            TelemetrySummary = telemetrySummary;
            Prompt = prompt;
        }
    }

    public void RecordError(string error)
    {
        lock (_lock)
        {
            LastRunUtc = DateTimeOffset.UtcNow;
            LastError = error;
        }
    }

    public CancellationToken GetDelayToken(CancellationToken stoppingToken)
    {
        lock (_lock)
        {
            if (_delayCts.IsCancellationRequested)
            {
                _delayCts.Dispose();
                _delayCts = new CancellationTokenSource();
            }

            return CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                _delayCts.Token).Token;
        }
    }

    public void TriggerImmediateRun()
    {
        lock (_lock)
        {
            if (!IsRunning && !_delayCts.IsCancellationRequested)
            {
                _delayCts.Cancel();
            }
        }
    }

    public StatusSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new StatusSnapshot(
                IsRunning,
                LastRunUtc,
                NextRunUtc,
                LastError,
                TelemetrySummary,
                Prompt,
                GridStatus,
                Decision);
        }
    }
}
