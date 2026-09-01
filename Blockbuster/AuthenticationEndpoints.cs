using System.Security.Claims;
using Blockbuster.Core.Profiles;
using Blockbuster.Core.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using BlockbusterAuthenticationOptions =
    Blockbuster.Infrastructure.Configuration.AuthenticationOptions;

namespace Blockbuster;

internal static class AuthenticationEndpoints
{
    public static void AddAuthentication(IServiceCollection services)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Blockbuster";
                options.DefaultChallengeScheme = "Blockbuster";
            })
            .AddPolicyScheme("Blockbuster", null, options =>
            {
                options.ForwardDefaultSelector = SelectAuthenticationScheme;
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

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Administrator", policy =>
                policy
                    .AddAuthenticationSchemes("Admin")
                    .RequireClaim("administrator", "true"));
        });
        services.AddCascadingAuthenticationState();
    }

    public static void Map(WebApplication app)
    {
        var group = app
            .MapGroup("/auth")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        group.MapPost("/profile/select", SignInProfileAsync);
        group.MapPost("/profile/clear", SignOutProfileAsync)
            .RequireAuthorization();
        group.MapPost("/admin/login", SignInAdministratorAsync);
        group.MapPost("/admin/logout", SignOutAdministratorAsync)
            .RequireAuthorization("Administrator");
    }

    private static string SelectAuthenticationScheme(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/admin")
            || context.Request.Path.StartsWithSegments("/auth/admin"))
        {
            return "Admin";
        }

        if (context.Request.Path.StartsWithSegments("/_blazor"))
        {
            if (Uri.TryCreate(
                    context.Request.Headers.Referer,
                    UriKind.Absolute,
                    out var referer)
                && string.Equals(
                    referer.Authority,
                    context.Request.Host.Value,
                    StringComparison.OrdinalIgnoreCase)
                && new PathString(referer.AbsolutePath).StartsWithSegments("/admin"))
            {
                return "Admin";
            }

            if (!context.Request.Cookies.ContainsKey("blockbuster.profile")
                && context.Request.Cookies.ContainsKey("blockbuster.admin"))
            {
                return "Admin";
            }
        }

        return "Profile";
    }

    private static async Task<IResult> SignInProfileAsync(
        HttpContext context,
        IProfileStore profiles,
        IPinHasher hasher)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Guid.TryParse(form["profileId"], out var profileId))
        {
            return Results.LocalRedirect("/profiles?error=Invalid+profile");
        }

        var profile = await profiles.GetAsync(profileId, context.RequestAborted);
        if (profile is null)
        {
            return Results.LocalRedirect("/profiles?error=Profile+not+found");
        }

        if (profile.HasPin)
        {
            var hash = await profiles.GetPinHashAsync(
                profileId,
                context.RequestAborted);
            if (hash is null || !hasher.Verify(form["pin"].ToString(), hash))
            {
                return Results.LocalRedirect("/profiles?error=Incorrect+PIN");
            }
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, profile.Id.ToString()),
                new Claim(ClaimTypes.Name, profile.Name)
            ],
            "Profile");
        await context.SignOutAsync("Admin");
        await context.SignInAsync(
            "Profile",
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });
        return Results.LocalRedirect("/movies");
    }

    private static async Task SignOutProfileAsync(HttpContext context)
    {
        await context.SignOutAsync("Profile");
        context.Response.Redirect("/");
    }

    private static async Task<IResult> SignInAdministratorAsync(
        HttpContext context,
        IAdministratorCredentialStore credentials,
        IPinHasher hasher,
        IOptions<BlockbusterAuthenticationOptions> authentication)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var hash = await credentials.GetHashAsync(context.RequestAborted);
        if (hash is null || !hasher.Verify(form["pin"].ToString(), hash))
        {
            return Results.LocalRedirect("/admin/login?error=Incorrect+PIN");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("administrator", "true"),
                new Claim(ClaimTypes.Name, "Administrator")
            ],
            "Admin");
        await context.SignOutAsync("Profile");
        await context.SignInAsync(
            "Admin",
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(
                    authentication.Value.AdminCookieLifetime)
            });
        return Results.LocalRedirect("/admin");
    }

    private static async Task SignOutAdministratorAsync(HttpContext context)
    {
        await context.SignOutAsync("Admin");
        context.Response.Redirect("/");
    }
}
