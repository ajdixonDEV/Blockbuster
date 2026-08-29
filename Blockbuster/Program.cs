using Blockbuster.Components;
using Blockbuster.Infrastructure.Configuration;
using BlazorBlueprint.Components;
using Serilog;
using Serilog.Events;
using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json;

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration["Storage:DataRoot"]))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:DataRoot"] = Path.Combine(builder.Environment.ContentRootPath, ".data")
    });
}

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddBlazorBlueprintComponents();
builder.Services.AddBlockbusterConfiguration(builder.Configuration);
builder.Services.AddSerilog((services, configuration) =>
{
    var paths = services.GetRequiredService<IStoragePathResolver>();
    Directory.CreateDirectory(paths.LogsPath);
    configuration.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services)
        .Enrich.FromLogContext().Enrich.WithProperty("Application", "Blockbuster")
        .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(Path.Combine(paths.LogsPath, "blockbuster-.log"), rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14, rollOnFileSizeLimit: true, fileSizeLimitBytes: 50 * 1024 * 1024,
            shared: true, formatProvider: CultureInfo.InvariantCulture);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (context, elapsed, exception) =>
    {
        if (exception is not null || context.Response.StatusCode >= 500) return LogEventLevel.Error;
        if (context.Request.Path.StartsWithSegments("/media") || context.Request.Path.StartsWithSegments("/_blazor")) return LogEventLevel.Debug;
        return elapsed > 1000 || context.Response.StatusCode >= 400 ? LogEventLevel.Warning : LogEventLevel.Information;
    };
    options.EnrichDiagnosticContext = (diagnostics, context) =>
    {
        diagnostics.Set("RequestHost", context.Request.Host.Value);
        diagnostics.Set("RequestScheme", context.Request.Scheme);
    };
});
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description })
        }, cancellationToken: context.RequestAborted);
    }
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Log.Information("Starting Blockbuster in {Environment}", app.Environment.EnvironmentName);
await app.RunAsync();
return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "Blockbuster terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
