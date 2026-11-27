namespace Cms.Server.Demo;

public sealed record DemoInfo(bool Enabled, double? ResetIntervalHours, DateTimeOffset? LastResetUtc, DateTimeOffset? NextResetUtc);

public static class DemoEndpoints
{
    /// <summary>
    /// In the public demo, block actions that would break the demo for other visitors until the
    /// next reset: every DELETE under /api, agent registration (demo PCs are simulated), and
    /// changes of staff accounts, passwords and club settings, also the reservation settings (visitors could
    /// lock others out or switch the booking page off).
    /// The club currency and the lock screen language are allowed: they change only the display, any
    /// visitor can set them back, and the next reset sets UAH and Ukrainian again.
    /// </summary>
    public static IApplicationBuilder UseDemoRestrictions(this IApplicationBuilder app, DemoOptions options)
    {
        if (!options.Enabled) return app;

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var write = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
            var blocked =
                (HttpMethods.IsDelete(request.Method) && request.Path.StartsWithSegments("/api")) ||
                (HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments("/api/devices/register")) ||
                (write && request.Path.StartsWithSegments("/api/staff")) ||
                (write && request.Path.StartsWithSegments("/api/settings") && !request.Path.StartsWithSegments("/api/settings/currency")
                    && !request.Path.StartsWithSegments("/api/settings/language")) ||
                (write && request.Path.StartsWithSegments("/api/reservations/settings")) ||
                (write && request.Path.StartsWithSegments("/api/auth/password")) ||
                (write && request.Path.StartsWithSegments("/api/auth/setup"));
            if (blocked)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "This action is disabled in the public demo." });
                return;
            }
            await next(context);
        });
    }

    /// <summary>GET /api/demo - demo status for the admin UI banner (enabled=false outside demo mode).</summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/demo", (DemoOptions options, DemoState state) => Results.Ok(new DemoInfo(
            options.Enabled,
            options.Enabled && options.ResetInterval > TimeSpan.Zero ? options.ResetInterval.TotalHours : null,
            options.Enabled ? state.LastResetUtc : null,
            options.Enabled ? state.NextResetUtc : null)))
            .AllowAnonymous();
        return app;
    }
}
