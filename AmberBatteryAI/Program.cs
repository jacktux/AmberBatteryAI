using System.Text;
using AmberBatteryAI.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PromptStorageService>();
builder.Services.AddSingleton<DecisionHistoryService>();
builder.Services.AddSingleton<TelemetryReader>();
builder.Services.AddSingleton<TelemetryHistoryService>();
builder.Services.AddSingleton<ProjectionStore>();
builder.Services.AddSingleton<EvGrabService>();
builder.Services.AddSingleton<DayProjectionService>();
builder.Services.AddHostedService<TelemetrySampler>();
builder.Services.AddSingleton<OptimizerState>();
builder.Services.AddHostedService<BatteryOptimizerWorker>();
builder.Services.AddHostedService<PriceWatcher>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.Use((context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath) &&
        !string.IsNullOrEmpty(ingressPath))
    {
        context.Request.PathBase = ingressPath.ToString();
    }
    return next();
});

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();

app.MapGet("/api/status", (OptimizerState state) =>
{
    if (state == null)
        return Results.BadRequest("OptimizerState is not available.");
    return Results.Ok(state.GetSnapshot());
});

app.MapPost("/api/run-now", (OptimizerState state) =>
{
    if (state == null)
        return Results.BadRequest("OptimizerState is not available.");
    state.TriggerImmediateRun();
    return Results.Accepted();
});

// Saved AI decisions: format=json (default), csv (summary for spreadsheets) or jsonl (everything, including prompts).
app.MapGet("/api/decisions", (DecisionHistoryService history, int? days, int? limit, string? format) =>
{
    var entries = history.Read(days ?? 7, limit);

    switch (format?.ToLowerInvariant())
    {
        case "csv":
            var csv = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(DecisionHistoryService.ToCsv(entries))).ToArray();
            return Results.File(csv, "text/csv; charset=utf-8", "ai-decisions.csv");
        case "jsonl":
            return Results.File(Encoding.UTF8.GetBytes(DecisionHistoryService.ToJsonLines(entries)), "application/x-ndjson", "ai-decisions.jsonl");
        default:
            return Results.Ok(entries);
    }
});

// Daily profit and loss from the logged telemetry samples: format=json (default) or csv.
app.MapGet("/api/pnl", (TelemetryHistoryService telemetry, int? days, string? format) =>
{
    var pnl = telemetry.BuildDailyPnl(days ?? 14);
    if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
    {
        var csv = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(PnlCalculator.ToCsv(pnl))).ToArray();
        return Results.File(csv, "text/csv; charset=utf-8", "daily-pnl.csv");
    }
    return Results.Ok(pnl);
});

// Actual import/export and cash per clock hour for one past or current local date (date=yyyy-MM-dd, default today).
app.MapGet("/api/pnl/hourly", (TelemetryHistoryService telemetry, string? date) =>
{
    var day = date is null ? DateOnly.FromDateTime(DateTime.Now) : DateOnly.TryParse(date, out var parsed) ? parsed : (DateOnly?)null;
    return day is null ? Results.BadRequest("date must be yyyy-MM-dd") : Results.Ok(telemetry.BuildHourlyPnl(day.Value));
});

// Rolling forecast bias (actual minus forecast feed-in price) per lead-time bucket. Recorded only, not used in decisions.
app.MapGet("/api/forecast-bias", (TelemetryHistoryService telemetry, int? days) =>
    Results.Ok(telemetry.BuildForecastBias(days ?? 14).Bias));

// Estimate of today's cash (realised so far plus a simulation of the rest of the day), rebuilt on every optimizer run.
// Display only. 204 until the first run has produced one. /api/projection/log lists every run's projection.
app.MapGet("/api/projection", (ProjectionStore store) =>
    store.ReadLatest() is { } projection ? Results.Ok(projection) : Results.NoContent());

app.MapGet("/api/projection/log", (ProjectionStore store, int? days) => Results.Ok(store.ReadLog(days ?? 1)));

app.MapGet("/api/prompt", (OptimizerState state) =>
{
    if (state == null)
        return Results.BadRequest("OptimizerState is not available.");
    var snapshot = state.GetSnapshot();
    return Results.Ok(new { customPrompt = snapshot.CustomPrompt });
});

app.MapPost("/api/prompt", async (OptimizerState state, HttpContext context) =>
{
    if (state == null)
        return Results.BadRequest("OptimizerState is not available.");

    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("customPrompt", out var promptProp))
        {
            string? customPrompt = promptProp.GetString();
            state.SetCustomPrompt(customPrompt);
            return Results.Ok(new { message = "Prompt updated successfully", customPrompt });
        }
        return Results.BadRequest("Missing customPrompt field in request body.");
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Invalid request: {ex.Message}");
    }
});

app.Run();
