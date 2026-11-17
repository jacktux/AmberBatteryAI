using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmberBatteryAI.Models;

namespace AmberBatteryAI.Services;

/// <summary>
/// Stores the periodic telemetry samples (samples-yyyy-MM-dd.jsonl) next to the decision log and writes the
/// reports derived from them (daily-pnl.csv / .json). Samples use the same retention as the decision log.
/// </summary>
public class TelemetryHistoryService
{
    private const string FilePrefix = "samples-";
    private const string FileExtension = ".jsonl";
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions LineOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly ILogger<TelemetryHistoryService> _logger;
    private readonly object _lock = new();
    private readonly TimeSpan _maxGap;
    private DateOnly _lastPruned;

    public TelemetryHistoryService(PromptStorageService promptStorage, IConfiguration config, ILogger<TelemetryHistoryService> logger)
    {
        _logger = logger;
        _directory = Path.GetDirectoryName(promptStorage.GetStoragePath()) ?? Path.GetTempPath();
        _retentionDays = Math.Max(1, config.GetValue("Optimizer:DecisionLogRetentionDays", 30));
        int sampleMinutes = config.GetValue("Telemetry:SampleIntervalMinutes", 5);
        _maxGap = TimeSpan.FromMinutes(Math.Clamp(sampleMinutes <= 0 ? 5 : sampleMinutes, 1, 60) * 3);
    }

    /// <summary>Appends one sample. Never throws: a logging problem must not stop anything else.</summary>
    public void Append(TelemetrySample sample)
    {
        try
        {
            string line = JsonSerializer.Serialize(sample, LineOptions) + "\n";
            lock (_lock)
            {
                File.AppendAllText(PathFor(DateOnly.FromDateTime(sample.Timestamp.DateTime)), line, Utf8NoBom);
                PruneOldFiles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the telemetry sample.");
        }
    }

    /// <summary>Samples for today and the previous days-1 days, oldest first.</summary>
    public List<TelemetrySample> Read(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = today.AddDays(-(Math.Clamp(days, 1, 365) - 1));
        var samples = new List<TelemetrySample>();

        lock (_lock)
        {
            for (var day = first; day <= today; day = day.AddDays(1))
            {
                string path = PathFor(day);
                if (!File.Exists(path)) continue;

                try
                {
                    foreach (string line in File.ReadLines(path, Utf8NoBom))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var sample = JsonSerializer.Deserialize<TelemetrySample>(line, LineOptions);
                            if (sample is not null) samples.Add(sample);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Skipping an unreadable line in {Path}.", path);
                        }
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not read {Path}.", path);
                }
            }
        }

        return samples;
    }

    public List<DailyPnl> BuildDailyPnl(int days) =>
        PnlCalculator.Daily(Read(days), DateTimeOffset.Now, _maxGap);

    /// <summary>Writes the reports next to the decision log. Never throws.</summary>
    public void WriteReports(IReadOnlyList<DailyPnl> pnl)
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(Path.Combine(_directory, "daily-pnl.csv"), PnlCalculator.ToCsv(pnl), Utf8NoBom);
                File.WriteAllText(Path.Combine(_directory, "daily-pnl.json"), JsonSerializer.Serialize(pnl, JsonOptions), Utf8NoBom);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the daily P&L report.");
        }
    }

    private string PathFor(DateOnly day) =>
        Path.Combine(_directory, FilePrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);

    private void PruneOldFiles()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_lastPruned == today) return;
        _lastPruned = today;

        var cutoff = today.AddDays(-_retentionDays);
        foreach (string file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            string name = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (!DateOnly.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= cutoff)
                continue;

            try { File.Delete(file); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete old telemetry file {File}.", file); }
        }
    }
}
