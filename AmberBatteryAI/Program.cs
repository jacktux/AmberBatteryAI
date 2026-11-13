using AmberBatteryAI.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
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

app.Run();
