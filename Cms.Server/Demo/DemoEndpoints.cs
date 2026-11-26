namespace Cms.Server.Demo;

public sealed record DemoInfo(bool Enabled, double? ResetIntervalHours, DateTimeOffset? LastResetUtc, DateTimeOffset? NextResetUtc);

public static class DemoEndpoints
{
    /// <summary>
    /// In the public demo, block actions that would break the demo for other visitors until the
    /// next reset: every DELETE under /api, and agent registration (demo PCs are simulated).
    /// </summary>
    public static IApplicationBuilder UseDemoRestrictions(this IApplicationBuilder app, DemoOptions options)
    {
        if (!options.Enabled) return app;

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var blocked =
                (HttpMethods.IsDelete(request.Method) && request.Path.StartsWithSegments("/api")) ||
                (HttpMethods.IsPost(request.Method) && request.Path.StartsWithSegments("/api/devices/register"));
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
            options.Enabled ? state.NextResetUtc : null)));
        return app;
    }
}
