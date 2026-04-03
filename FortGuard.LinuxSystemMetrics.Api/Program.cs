using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FortGuard.LinuxSystemMetrics.Api.Metrics;

var builder = WebApplication.CreateBuilder(args);

var urls = builder.Configuration["ASPNETCORE_URLS"]
    ?? builder.Configuration["Urls"]
    ?? "http://0.0.0.0:8099";
builder.WebHost.UseUrls(urls);

builder.Services.AddSingleton<LinuxMetricsCollector>();

var app = builder.Build();

var jsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
};

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/v1/", StringComparison.Ordinal))
    {
        await next().ConfigureAwait(false);
        return;
    }

    var token = app.Configuration["Auth:Token"]?.Trim();
    if (string.IsNullOrEmpty(token))
    {
        await next().ConfigureAwait(false);
        return;
    }

    var auth = context.Request.Headers.Authorization.ToString();
    var prefix = "Bearer ";
    var provided = auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && auth.Length > prefix.Length
        ? auth[prefix.Length..].Trim()
        : "";
    if (!string.Equals(provided, token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }

    await next().ConfigureAwait(false);
});

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapGet("/api/v1/metrics", async (LinuxMetricsCollector collector, CancellationToken ct) =>
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        return Results.Problem(
            detail: "This API only collects metrics on Linux.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var data = await collector.CollectAsync(ct).ConfigureAwait(false);
    return Results.Json(data, jsonOptions);
});

app.MapGet("/api/v1/metrics/summary", async (LinuxMetricsCollector collector, CancellationToken ct) =>
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        return Results.Problem(
            detail: "This API only collects metrics on Linux.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var full = await collector.CollectAsync(ct).ConfigureAwait(false);
    var summary = new
    {
        full.Timestamp,
        cpu_percent = full.Cpu.PercentTotal,
        memory_percent = full.Memory.Percent,
        disk_root_percent = RootDiskPercent(full),
        process_count = full.Processes.Count,
    };
    return Results.Json(summary, jsonOptions);
});

app.Run();

static double? RootDiskPercent(MetricsRootDto m)
{
    var parts = m.Disk.Partitions;
    if (parts.Count == 0)
        return null;
    foreach (var p in parts)
    {
        var mp = (p.Mountpoint ?? "").TrimEnd('/');
        if (mp is "" or "/")
            return p.PercentUsed;
    }
    return parts[0].PercentUsed;
}
