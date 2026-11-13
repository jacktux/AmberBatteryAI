using AmberBatteryAI.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PromptStorageService>();
builder.Services.AddSingleton<OptimizerState>();
builder.Services.AddHostedService<BatteryOptimizerWorker>();

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
