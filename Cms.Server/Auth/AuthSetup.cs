using System.Security.Claims;
using System.Threading.RateLimiting;
using Cms.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace Cms.Server.Auth;

public static class AuthConstants
{
    public const string CookieName = "ccms_auth";
    /// <summary>
    /// Every state-changing request that carries the login cookie must send this header with the
    /// value "1". A cross-site form or script cannot add a custom header without a CORS preflight,
    /// and CORS is same-origin by default (CSRF protection on top of SameSite=Strict).
    /// </summary>
    public const string CsrfHeader = "X-CCMS-CSRF";
    public const string StampClaim = "ccms:stamp";
    public const string DisplayNameClaim = "ccms:display_name";
    public const string AuthRateLimitPolicy = "auth";
    /// <summary>Requests of the public booking page per client IP (Booking:RequestsPerIpPer10Minutes, default 5 in 10 minutes).</summary>
    public const string BookingWritePolicy = "booking-write";
    /// <summary>Reads of the public booking page per client IP (Booking:ReadsPerIpPerMinute, default 120 a minute).</summary>
    public const string BookingReadPolicy = "booking-read";
}

/// <summary>
/// Staff authentication: HTTP-only, SameSite=Strict cookie (ASP.NET Core cookie authentication,
/// encrypted and signed with Data Protection keys stored in the data directory).
///   Auth:SessionHours          - login lifetime, sliding (default 12)
///   Auth:CookieSecure          - "always" = cookie only over HTTPS; default "auto" (same as the request)
///   Auth:LoginRateLimitPerMinute - login/setup attempts per client IP per minute (default 20; demo 300)
///   ReverseProxy:TrustForwardedHeaders / ReverseProxy:ForwardLimit - take client IP and scheme from
///                                X-Forwarded-For / X-Forwarded-Proto (set in the Docker image)
///   Cors:AllowedOrigins        - extra origins for a UI on another origin (default: none = same origin only)
/// </summary>
public static class AuthSetup
{
    public static void AddStaffAuth(this WebApplicationBuilder builder, ServerPaths paths, bool demoMode)
    {
        var config = builder.Configuration;
        var services = builder.Services;

        services.AddSingleton(paths);
        services.AddSingleton<SetupState>();
        services.AddSingleton(new LoginThrottle { Enabled = !demoMode });
        services.AddHttpContextAccessor();
        services.AddScoped<AuditLogger>();

        services.AddDataProtection()
            .SetApplicationName("PCClub.Cms.Server")
            .PersistKeysToFileSystem(new DirectoryInfo(paths.Combine("keys")));

        var hours = Math.Clamp(config.GetValue("Auth:SessionHours", 12.0), 0.25, 24 * 7);
        var secureAlways = string.Equals(config["Auth:CookieSecure"], "always", StringComparison.OrdinalIgnoreCase);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = AuthConstants.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = secureAlways ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(hours);
                options.SlidingExpiration = true;
                // An API: answer with status codes, never redirect to a login page
                options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
            })
            .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(DeviceKeyAuthenticationHandler.SchemeName, null);
        services.AddSingleton(DeviceEnrollmentOptions.FromConfiguration(config));

        services.AddAuthorization(options =>
        {
            foreach (var (name, roles) in Policies.Roles)
            {
                options.AddPolicy(name, policy => policy
                    .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .RequireRole(roles));
            }
            options.AddPolicy(Policies.Device, policy => policy
                .AddAuthenticationSchemes(DeviceKeyAuthenticationHandler.SchemeName)
                .RequireClaim(DeviceKeyAuthenticationHandler.DeviceIdClaim));
            options.AddPolicy(Policies.ApprovedDevice, policy => policy
                .AddAuthenticationSchemes(DeviceKeyAuthenticationHandler.SchemeName)
                .RequireClaim(DeviceKeyAuthenticationHandler.DeviceIdClaim)
                .RequireClaim(DeviceKeyAuthenticationHandler.StatusClaim, DeviceStatuses.Approved));
            // Every endpoint without its own attribute needs a signed-in staff member
            options.FallbackPolicy = new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireRole(StaffRoles.All)
                .Build();
        });

        var perMinute = config.GetValue("Auth:LoginRateLimitPerMinute", demoMode ? 300 : 20);
        var bookingWrites = Math.Max(1, config.GetValue("Booking:RequestsPerIpPer10Minutes", 5));
        var bookingReads = Math.Max(1, config.GetValue("Booking:ReadsPerIpPerMinute", 120));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AuthConstants.BookingWritePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = bookingWrites, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            options.AddPolicy(AuthConstants.BookingReadPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = bookingReads, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy(AuthConstants.AuthRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, perMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });

        if (config.GetValue("ReverseProxy:TrustForwardedHeaders", false))
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = Math.Max(1, config.GetValue("ReverseProxy:ForwardLimit", 1));
                // The server is reachable only through the proxy (Docker network, ingress)
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }
    }

    public static ClaimsPrincipal CreatePrincipal(StaffUserEntity staff)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, staff.Id.ToString()),
            new Claim(ClaimTypes.Name, staff.Username),
            new Claim(ClaimTypes.Role, staff.Role),
            new Claim(AuthConstants.DisplayNameClaim, staff.DisplayName),
            new Claim(AuthConstants.StampClaim, staff.SecurityStamp),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static Task SignInStaffAsync(this HttpContext http, StaffUserEntity staff) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(staff),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });

    /// <summary>
    /// Runs on every request with a login cookie: the account must still exist, be active and have
    /// the same role and security stamp. A password change, role change or deactivation therefore
    /// ends all existing logins of that account at once.
    /// </summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var valid = false;
        if (principal != null && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<CmsDbContext>();
            var staff = await db.StaffUsers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            valid = staff is { IsActive: true }
                    && staff.SecurityStamp == principal.FindFirstValue(AuthConstants.StampClaim)
                    && staff.Role == principal.FindFirstValue(ClaimTypes.Role);
        }
        if (!valid)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    /// <summary>
    /// CSRF protection for cookie-authenticated API calls: unsafe methods must carry the
    /// X-CCMS-CSRF header, and a browser Origin header must match this host or Cors:AllowedOrigins.
    /// Also applies to /api/auth (login CSRF). Agent and kiosk calls carry no cookie and are not affected.
    /// </summary>
    public static IApplicationBuilder UseCsrfProtection(this IApplicationBuilder app, IReadOnlyCollection<string> allowedOrigins)
    {
        var origins = new HashSet<string>(allowedOrigins.Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
                                 HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));
            if (unsafeMethod && request.Path.StartsWithSegments("/api") &&
                (request.Cookies.ContainsKey(AuthConstants.CookieName) || request.Path.StartsWithSegments("/api/auth")))
            {
                var headerOk = string.Equals(request.Headers[AuthConstants.CsrfHeader].ToString(), "1", StringComparison.Ordinal);
                if (!headerOk || !OriginAllowed(request, origins))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "CSRF check failed: send the X-CCMS-CSRF: 1 header from the admin UI origin." });
                    return;
                }
            }
            await next(context);
        });
    }

    private static bool OriginAllowed(HttpRequest request, HashSet<string> allowed)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // not a cross-origin browser request
        if (allowed.Contains(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false; // includes "null"
        return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ClientIp(this HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
