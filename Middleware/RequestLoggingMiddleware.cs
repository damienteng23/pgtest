using System.Diagnostics;

namespace pgtest.Middleware
{
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(
            RequestDelegate next,
            ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var request = context.Request;

            try
            {
                await _next(context);
                stopwatch.Stop();

                // log successful requests
                _logger.LogInformation(
                    "HTTP {Method} {Path} → {StatusCode} in {Duration}ms",
                    request.Method,
                    request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // log failed requests
                _logger.LogError(ex,
                    "HTTP {Method} {Path} → {StatusCode} in {Duration}ms | Error: {Error}",
                    request.Method,
                    request.Path,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    ex.Message);

                throw;
            }
        }
    }
}