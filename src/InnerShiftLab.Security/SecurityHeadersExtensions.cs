// =============================================================================
//  Security headers — cheap, applied to everything including 401s and 429s.
// =============================================================================
namespace InnerShiftLab.Security;

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                // API responses carry revenue/attribution data — never cache them.
                headers["Cache-Control"] = "no-store";
                headers.Remove("Server");
                return Task.CompletedTask;
            });
            await next();
        });
}
