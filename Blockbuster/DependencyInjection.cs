using System.Globalization;
using System.Net;
using Blockbuster.Components;
using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Operations;
using Blockbuster.Infrastructure.Persistence;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

namespace Blockbuster;

public static class DependencyInjection
{
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

    public static WebApplicationBuilder AddBlockbusterWeb(this WebApplicationBuilder builder)
    {
        builder.Host.UseWindowsService(options => options.ServiceName = "Blockbuster");
        builder.Host.UseSystemd();

        if (builder.Environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(builder.Configuration["Storage:DataRoot"]))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = Path.Combine(builder.Environment.ContentRootPath, ".data")
            });
        }

        ResolveRelativeLibraryRoots(builder);

        builder.Services
            .AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSignalR();
        builder.Services.AddBlazorBlueprintComponents();
        builder.Services.AddBlockbusterInfrastructure(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        AuthenticationEndpoints.AddAuthentication(builder.Services);
        AddLogging(builder);
        return builder;
    }

    public static WebApplication UseBlockbusterWeb(this WebApplication app)
    {
        UseForwardedHeaders(app);
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (context, elapsed, exception) =>
            {
                if (exception is not null || context.Response.StatusCode >= 500)
                {
                    return LogEventLevel.Error;
                }

                if (context.Request.Path.StartsWithSegments("/media")
                    || context.Request.Path.StartsWithSegments("/_blazor"))
                {
                    return LogEventLevel.Debug;
                }

                return elapsed > 1000 || context.Response.StatusCode >= 400
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
            };
            options.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                diagnostics.Set("RequestHost", context.Request.Host.Value);
                diagnostics.Set("RequestScheme", context.Request.Scheme);
            };
        });

        app.UseHttpsRedirection();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();

        HealthEndpoints.Map(app);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        AuthenticationEndpoints.Map(app);
        AdministrationEndpoints.Map(app);
        PlaybackEndpoints.Map(app);
        SharedRoomEndpoints.Map(app);
        return app;
    }

    public static async Task<int> RunBlockbusterOperatorAsync(
        this WebApplication app,
        string[] args)
    {
        await app.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync();
        return await app.Services
            .GetRequiredService<OperatorCommandDispatcher>()
            .RunAsync(args);
    }

    private static void ResolveRelativeLibraryRoots(WebApplicationBuilder builder)
    {
        var resolvedRoots = builder.Configuration
            .GetSection("Libraries:Sources")
            .GetChildren()
            .SelectMany(source => source.GetSection("MovieRoots").GetChildren())
            .Where(root =>
                !string.IsNullOrWhiteSpace(root.Value)
                && !Path.IsPathFullyQualified(root.Value))
            .ToDictionary(
                root => root.Path,
                root => (string?)Path.GetFullPath(
                    root.Value!,
                    builder.Environment.ContentRootPath),
                StringComparer.OrdinalIgnoreCase);

        if (resolvedRoots.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(resolvedRoots);
        }
    }

    private static void AddLogging(WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) =>
        {
            var paths = services.GetRequiredService<IStoragePathResolver>();
            Directory.CreateDirectory(paths.LogsPath);
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "Blockbuster")
                .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.File(
                    Path.Combine(paths.LogsPath, "blockbuster-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: 50 * 1024 * 1024,
                    shared: true,
                    formatProvider: CultureInfo.InvariantCulture);
        });
    }

    private static void UseForwardedHeaders(WebApplication app)
    {
        var reverseProxy = app.Services
            .GetRequiredService<IOptions<ReverseProxyOptions>>()
            .Value;
        if (!reverseProxy.Enabled)
        {
            return;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = reverseProxy.ForwardLimit
        };

        foreach (var proxy in reverseProxy.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        app.UseForwardedHeaders(options);
    }
}
