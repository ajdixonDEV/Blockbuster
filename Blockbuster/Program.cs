using Blockbuster.Components;
using Blockbuster.Infrastructure.Configuration;
using BlazorBlueprint.Components;
using Serilog;
using Serilog.Events;
using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json;
using Blockbuster.Infrastructure.Operations;
using Blockbuster.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Blockbuster.Core.Profiles;
using Blockbuster.Core.Security;
using Blockbuster.Infrastructure.Security;

Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    var isOperatorCommand = args.Length > 0 && string.Equals(args[0], "operator", StringComparison.OrdinalIgnoreCase);
    var builder = WebApplication.CreateBuilder(isOperatorCommand ? [] : args);
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

    builder.Services
        .AddRazorComponents()
        .AddInteractiveServerComponents();
    builder.Services.AddBlazorBlueprintComponents();
    builder.Services.AddBlockbusterConfiguration(builder.Configuration);
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "Profile";
        options.DefaultChallengeScheme = "Profile";
    })
        .AddCookie("Profile", options =>
        {
            options.Cookie.Name = "blockbuster.profile";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/profiles";
            options.ExpireTimeSpan = TimeSpan.FromDays(3650);
            options.SlidingExpiration = false;
        })
        .AddCookie("Admin", options =>
        {
            options.Cookie.Name = "blockbuster.admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.LoginPath = "/admin/login";
            options.AccessDeniedPath = "/admin/login";
            options.SlidingExpiration = false;
        });
    builder.Services.AddAuthorization(options =>
        options.AddPolicy("Administrator", policy => policy.AddAuthenticationSchemes("Admin").RequireClaim("administrator", "true")));
    builder.Services.AddCascadingAuthenticationState();
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

    if (isOperatorCommand)
    {
        await app.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync();
        return await app.Services.GetRequiredService<OperatorCommandDispatcher>().RunAsync(args[1..]);
    }

    var reverseProxy = app.Services.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
    if (reverseProxy.Enabled)
    {
        var forwardedHeaders = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = reverseProxy.ForwardLimit
        };
        foreach (var proxy in reverseProxy.KnownProxies)
            forwardedHeaders.KnownProxies.Add(IPAddress.Parse(proxy));
        app.UseForwardedHeaders(forwardedHeaders);
    }

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
    app.UseAuthentication();
    app.UseAuthorization();
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

    app.MapPost("/auth/profile/select", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["profileId"], out var profileId)) return Results.LocalRedirect("/profiles?error=Invalid+profile");
        var profile = await profiles.GetAsync(profileId, context.RequestAborted);
        if (profile is null) return Results.LocalRedirect("/profiles?error=Profile+not+found");
        if (profile.HasPin)
        {
            var hash = await profiles.GetPinHashAsync(profileId, context.RequestAborted);
            if (hash is null || !hasher.Verify(form["pin"].ToString(), hash)) return Results.LocalRedirect("/profiles?error=Incorrect+PIN");
        }
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, profile.Id.ToString()), new Claim(ClaimTypes.Name, profile.Name)], "Profile");
        await context.SignInAsync("Profile", new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
        return Results.LocalRedirect("/movies");
    }).DisableAntiforgery();

    app.MapPost("/auth/profile/clear", async (HttpContext context) =>
    {
        await context.SignOutAsync("Profile");
        return Results.LocalRedirect("/profiles");
    }).DisableAntiforgery();

    app.MapPost("/auth/admin/login", async (HttpContext context, IAdministratorCredentialStore credentials, IPinHasher hasher, IOptions<Blockbuster.Infrastructure.Configuration.AuthenticationOptions> auth) =>
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var hash = await credentials.GetHashAsync(context.RequestAborted);
        if (hash is null || !hasher.Verify(form["pin"].ToString(), hash)) return Results.LocalRedirect("/admin/login?error=Incorrect+PIN");
        var identity = new ClaimsIdentity([new Claim("administrator", "true"), new Claim(ClaimTypes.Name, "Administrator")], "Admin");
        await context.SignInAsync("Admin", new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(auth.Value.AdminCookieLifetime)
        });
        return Results.LocalRedirect("/admin");
    }).DisableAntiforgery();

    app.MapPost("/auth/admin/logout", async (HttpContext context) =>
    {
        await context.SignOutAsync("Admin");
        return Results.LocalRedirect("/admin/login");
    }).DisableAntiforgery();

    app.MapPost("/admin/profiles/create", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
    {
        var admin = await context.AuthenticateAsync("Admin");
        if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var pin = form["pin"].ToString();
        try { await profiles.CreateAsync(form["name"].ToString(), string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin), context.RequestAborted); }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException) { return Results.LocalRedirect("/admin?error=" + Uri.EscapeDataString("Profile name or PIN is invalid or already used.")); }
        return Results.LocalRedirect("/admin");
    }).DisableAntiforgery();

    app.MapPost("/admin/profiles/update", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
    {
        var admin = await context.AuthenticateAsync("Admin");
        if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["id"], out var id)) return Results.BadRequest();
        var pin = form["pin"].ToString();
        try { await profiles.UpdateAsync(id, form["name"].ToString(), string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin), form["clearPin"] == "on", context.RequestAborted); }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException) { return Results.LocalRedirect("/admin?error=" + Uri.EscapeDataString("Profile name or PIN is invalid or already used.")); }
        return Results.LocalRedirect("/admin");
    }).DisableAntiforgery();

    app.MapPost("/admin/profiles/delete", async (HttpContext context, IProfileStore profiles) =>
    {
        var admin = await context.AuthenticateAsync("Admin");
        if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (Guid.TryParse(form["id"], out var id)) await profiles.DeleteAsync(id, context.RequestAborted);
        return Results.LocalRedirect("/admin");
    }).DisableAntiforgery();

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
