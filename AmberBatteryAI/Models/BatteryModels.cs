using System.Text.Json.Serialization;

namespace AmberBatteryAI.Models;

public record OptimizerDecision(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("export_price")] double ExportPrice,
    [property: JsonPropertyName("export_stop_soc")] double ExportStopSoc,
    [property: JsonPropertyName("export_power_limit")] double ExportPowerLimit,
    [property: JsonPropertyName("import_price")] double ImportPrice,
    [property: JsonPropertyName("import_stop_soc")] double ImportStopSoc,
    [property: JsonPropertyName("import_power_limit")] double ImportPowerLimit,
    [property: JsonPropertyName("reasoning")] string Reasoning
);

public record AppliedDecision(
    string Mode,
    double ExportPrice,
    double ExportStopSoc,
    double ExportPowerLimit,
    double ImportPrice,
    double ImportStopSoc,
    double ImportPowerLimit,
    string Reasoning
);

public record StatusSnapshot(
    bool IsRunning,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc,
    string? LastError,
    string? TelemetrySummary,
    string? Prompt,
    string? GridStatus,
    AppliedDecision? Decision
);
