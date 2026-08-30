using Blockbuster.Components;
using Blockbuster.Core.Movies;
using Blockbuster.Core.Playback;
using Blockbuster.Core.Profiles;
using Blockbuster.Core.Scanning;
using Blockbuster.Core.Security;
using Blockbuster.Infrastructure;
using Blockbuster.Infrastructure.Configuration;
using Blockbuster.Infrastructure.Movies;
using Blockbuster.Infrastructure.Operations;
using Blockbuster.Infrastructure.Persistence;
using Blockbuster.Infrastructure.Security;
using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using BlockbusterAuthenticationOptions = Blockbuster.Infrastructure.Configuration.AuthenticationOptions;
using Blockbuster.Core.SharedPlayback;
using Blockbuster.SharedPlayback;

namespace Blockbuster;

public static class DependencyInjection
{
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration().MinimumLevel.Information()
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

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddSignalR();
        builder.Services.AddBlazorBlueprintComponents();
        builder.Services.AddBlockbusterInfrastructure(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        AddAuthentication(builder.Services);
        AddLogging(builder);
        return builder;
    }

    private static void ResolveRelativeLibraryRoots(WebApplicationBuilder builder)
    {
        var resolvedRoots = builder.Configuration
            .GetSection("Libraries:Sources")
            .GetChildren()
            .SelectMany(source => source.GetSection("MovieRoots").GetChildren())
            .Where(root => !string.IsNullOrWhiteSpace(root.Value) && !Path.IsPathFullyQualified(root.Value))
            .ToDictionary(
                root => root.Path,
                root => (string?)Path.GetFullPath(root.Value!, builder.Environment.ContentRootPath),
                StringComparer.OrdinalIgnoreCase);

        if (resolvedRoots.Count > 0)
            builder.Configuration.AddInMemoryCollection(resolvedRoots);
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
                if (exception is not null || context.Response.StatusCode >= 500) return LogEventLevel.Error;
                if (context.Request.Path.StartsWithSegments("/media")
                    || context.Request.Path.StartsWithSegments("/_blazor")) return LogEventLevel.Debug;
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
        MapHealthEndpoints(app);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        MapAuthenticationEndpoints(app);
        MapAdministrationEndpoints(app);
        MapPlaybackEndpoints(app);
        app.MapHub<SharedPlaybackHub>("/hubs/shared-playback");
        return app;
    }

    private static void MapPlaybackEndpoints(WebApplication app)
    {
        app.MapMethods("/media/{mediaFileId:guid}", ["GET", "HEAD"], async (Guid mediaFileId, IMovieLibrary library, CancellationToken cancellationToken) =>
        {
            var source = await library.AuthorizeStreamAsync(mediaFileId, cancellationToken);
            return source is null
                ? Results.NotFound()
                : Results.File(source.FullPath, source.ContentType, enableRangeProcessing: true, lastModified: source.LastModified);
        }).RequireAuthorization();

        app.MapGet("/artwork/{movieId:guid}/{kind}", async (Guid movieId, string kind, IMovieLibrary library, CancellationToken cancellationToken) =>
        {
            var source = await library.GetArtworkAsync(movieId, kind, cancellationToken);
            return source is null ? Results.NotFound() : Results.File(source.FullPath, source.ContentType, lastModified: source.LastModified);
        }).RequireAuthorization();

        app.MapPost("/api/movies/{movieId:guid}/progress", async (Guid movieId, ProgressUpdate update, HttpContext context, IPlaybackProgressStore progress, CancellationToken cancellationToken) =>
        {
            var claim = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(claim, out var profileId)) return Results.Unauthorized();
            if (!double.IsFinite(update.PositionSeconds) || update.PositionSeconds < 0) return Results.BadRequest();
            var result = await progress.SaveAsync(profileId, movieId, TimeSpan.FromSeconds(update.PositionSeconds), update.ExpectedRevision, update.EventType ?? "progress", cancellationToken);
            return result.Accepted ? Results.Ok(new { revision = result.Current.Revision, positionSeconds = result.Current.Position.TotalSeconds }) : Results.Conflict(new { revision = result.Current.Revision, positionSeconds = result.Current.Position.TotalSeconds });
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/api/shared", async (CreateRoomRequest request, HttpContext context,
            IMovieLibrary library, ISharedPlaybackCoordinator rooms, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var profileId)) return Results.Unauthorized();
            var movie = await library.GetAsync(request.MovieId, profileId, cancellationToken);
            var version = movie?.Versions.FirstOrDefault(item => item.MediaFileId == request.MediaFileId && item.IsAvailable);
            if (movie is null || version is null) return Results.BadRequest();
            var room = rooms.CreateRoom(movie.Id, version.MediaFileId, movie.Title);
            return Results.Ok(new { roomId = room.RoomId });
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/shared/new", async (HttpContext context, IMovieLibrary library,
            ISharedPlaybackCoordinator rooms, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var profileId))
                return Results.Unauthorized();
            var form = await context.Request.ReadFormAsync(cancellationToken);
            if (!Guid.TryParse(form["movie"], out var movieId) || !Guid.TryParse(form["file"], out var mediaFileId))
                return Results.BadRequest();
            var movie = await library.GetAsync(movieId, profileId, cancellationToken);
            var version = movie?.Versions.FirstOrDefault(item => item.MediaFileId == mediaFileId && item.IsAvailable);
            if (movie is null || version is null) return Results.BadRequest();
            var room = rooms.CreateRoom(movie.Id, version.MediaFileId, movie.Title);
            return Results.LocalRedirect($"/shared/{room.RoomId}");
        }).RequireAuthorization().DisableAntiforgery();
    }

    private sealed record ProgressUpdate(double PositionSeconds, long ExpectedRevision, string? EventType);
    private sealed record CreateRoomRequest(Guid MovieId, Guid MediaFileId);

    public static async Task<int> RunBlockbusterOperatorAsync(this WebApplication app, string[] args)
    {
        await app.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync();
        return await app.Services.GetRequiredService<OperatorCommandDispatcher>().RunAsync(args);
    }

    private static void AddAuthentication(IServiceCollection services)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "Blockbuster";
            options.DefaultChallengeScheme = "Blockbuster";
        })
        .AddPolicyScheme("Blockbuster", null, options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                if (context.Request.Path.StartsWithSegments("/admin")
                    || context.Request.Path.StartsWithSegments("/auth/admin")) return "Admin";

                if (context.Request.Path.StartsWithSegments("/_blazor"))
                {
                    if (Uri.TryCreate(context.Request.Headers.Referer, UriKind.Absolute, out var referer)
                        && string.Equals(referer.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
                        && new PathString(referer.AbsolutePath).StartsWithSegments("/admin")) return "Admin";

                    if (!context.Request.Cookies.ContainsKey("blockbuster.profile")
                        && context.Request.Cookies.ContainsKey("blockbuster.admin")) return "Admin";
                }
                return "Profile";
            };
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
        services.AddAuthorization(options => options.AddPolicy("Administrator", policy =>
            policy.AddAuthenticationSchemes("Admin").RequireClaim("administrator", "true")));
        services.AddCascadingAuthenticationState();
    }

    private static void AddLogging(WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) =>
        {
            var paths = services.GetRequiredService<IStoragePathResolver>();
            Directory.CreateDirectory(paths.LogsPath);
            configuration.ReadFrom.Configuration(builder.Configuration).ReadFrom.Services(services)
                .Enrich.FromLogContext().Enrich.WithProperty("Application", "Blockbuster")
                .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.File(Path.Combine(paths.LogsPath, "blockbuster-.log"),
                    rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                    rollOnFileSizeLimit: true, fileSizeLimitBytes: 50 * 1024 * 1024,
                    shared: true, formatProvider: CultureInfo.InvariantCulture);
        });
    }

    private static void UseForwardedHeaders(WebApplication app)
    {
        var reverseProxy = app.Services.GetRequiredService<IOptions<ReverseProxyOptions>>().Value;
        if (!reverseProxy.Enabled) return;
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = reverseProxy.ForwardLimit
        };
        foreach (var proxy in reverseProxy.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
        app.UseForwardedHeaders(options);
    }

    private static void MapHealthEndpoints(WebApplication app)
    {
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
                    checks = report.Entries.ToDictionary(entry => entry.Key,
                        entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description })
                }, cancellationToken: context.RequestAborted);
            }
        });
    }

    private static void MapAuthenticationEndpoints(WebApplication app)
    {
        app.MapPost("/auth/profile/select", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["profileId"], out var profileId))
                return Results.LocalRedirect("/profiles?error=Invalid+profile");
            var profile = await profiles.GetAsync(profileId, context.RequestAborted);
            if (profile is null) return Results.LocalRedirect("/profiles?error=Profile+not+found");
            if (profile.HasPin)
            {
                var hash = await profiles.GetPinHashAsync(profileId, context.RequestAborted);
                if (hash is null || !hasher.Verify(form["pin"].ToString(), hash))
                    return Results.LocalRedirect("/profiles?error=Incorrect+PIN");
            }
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, profile.Id.ToString()), new Claim(ClaimTypes.Name, profile.Name)],
                "Profile");
            await context.SignInAsync("Profile", new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = false });
            return Results.LocalRedirect("/movies");
        }).DisableAntiforgery();

        app.MapPost("/auth/profile/clear", async (HttpContext context) =>
        {
            await context.SignOutAsync("Profile");
            return Results.LocalRedirect("/profiles");
        }).DisableAntiforgery();

        app.MapPost("/auth/admin/login", async (HttpContext context, IAdministratorCredentialStore credentials,
            IPinHasher hasher, IOptions<BlockbusterAuthenticationOptions> authentication) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var hash = await credentials.GetHashAsync(context.RequestAborted);
            if (hash is null || !hasher.Verify(form["pin"].ToString(), hash))
                return Results.LocalRedirect("/admin/login?error=Incorrect+PIN");
            var identity = new ClaimsIdentity(
                [new Claim("administrator", "true"), new Claim(ClaimTypes.Name, "Administrator")], "Admin");
            await context.SignInAsync("Admin", new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(authentication.Value.AdminCookieLifetime)
            });
            return Results.LocalRedirect("/admin");
        }).DisableAntiforgery();

        app.MapPost("/auth/admin/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync("Admin");
            return Results.LocalRedirect("/admin/login");
        }).DisableAntiforgery();
    }

    private static void MapAdministrationEndpoints(WebApplication app)
    {
        app.MapPost("/admin/profiles/create", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
        {
            var admin = await context.AuthenticateAsync("Admin");
            if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var pin = form["pin"].ToString();
            try
            {
                await profiles.CreateAsync(form["name"].ToString(),
                    string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin), context.RequestAborted);
            }
            catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.LocalRedirect("/admin?error="
                    + Uri.EscapeDataString("Profile name or PIN is invalid or already used."));
            }
            return Results.LocalRedirect("/admin");
        }).DisableAntiforgery();

        app.MapPost("/admin/profiles/update", async (HttpContext context, IProfileStore profiles, IPinHasher hasher) =>
        {
            var admin = await context.AuthenticateAsync("Admin");
            if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["id"], out var id)) return Results.BadRequest();
            var pin = form["pin"].ToString();
            try
            {
                await profiles.UpdateAsync(id, form["name"].ToString(),
                    string.IsNullOrEmpty(pin) ? null : hasher.Hash(pin), form["clearPin"] == "on",
                    context.RequestAborted);
            }
            catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                return Results.LocalRedirect("/admin?error="
                    + Uri.EscapeDataString("Profile name or PIN is invalid or already used."));
            }
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

        app.MapPost("/admin/scans/request", async (HttpContext context, ILibraryScanner scanner) =>
        {
            var admin = await context.AuthenticateAsync("Admin");
            if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
            var result = await scanner.ScanAsync(ScanReason.Manual, context.RequestAborted);
            if (!result.Succeeded)
                return Results.LocalRedirect("/admin?error=" + Uri.EscapeDataString(
                    "One or more movie roots could not be scanned. Existing availability was preserved for failed roots."));
            return Results.LocalRedirect("/admin");
        }).DisableAntiforgery();

        app.MapPost("/admin/matches/accept", async (HttpContext context, IMovieMatchResolver resolver) =>
        {
            var admin = await context.AuthenticateAsync("Admin");
            if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["mediaFileId"], out var mediaFileId)
                || !int.TryParse(form["tmdbId"], out var tmdbId) || tmdbId <= 0) return Results.BadRequest();
            var result = await resolver.ResolveProviderSelectionAsync(mediaFileId, tmdbId, context.RequestAborted);
            return result.Succeeded ? Results.LocalRedirect("/admin") : Results.LocalRedirect("/admin?error=" + Uri.EscapeDataString(result.Message ?? "That TMDB movie could not be found."));
        }).DisableAntiforgery();

        app.MapPost("/admin/matches/local", async (HttpContext context, IMovieMatchResolver resolver) =>
        {
            var admin = await context.AuthenticateAsync("Admin");
            if (!admin.Succeeded) return Results.Challenge(authenticationSchemes: ["Admin"]);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["mediaFileId"], out var mediaFileId)) return Results.BadRequest();
            int? year = int.TryParse(form["year"], out var parsedYear) ? parsedYear : null;
            var result = await resolver.ResolveLocalMetadataAsync(mediaFileId, form["title"].ToString(), year, context.RequestAborted);
            return result.Succeeded ? Results.LocalRedirect("/admin") : Results.LocalRedirect("/admin?error=" + Uri.EscapeDataString(result.Message ?? "The local title or year is invalid."));
        }).DisableAntiforgery();
    }
}
