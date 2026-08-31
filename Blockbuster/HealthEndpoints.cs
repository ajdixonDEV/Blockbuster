using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Blockbuster;

internal static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResponseWriter = WriteResponseAsync
            });
    }

    private static Task WriteResponseAsync(
        HttpContext context,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new
                    {
                        status = entry.Value.Status.ToString(),
                        description = entry.Value.Description
                    })
            },
            cancellationToken: context.RequestAborted);
    }
}
