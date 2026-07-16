using System.Diagnostics;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

public class HealthCheckerService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HealthCheckerService> _logger;

    public HealthCheckerService(IHttpClientFactory httpClientFactory, ILogger<HealthCheckerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<StatusCheck> CheckAsync(string environment, string serviceName, CheckType checkType, string url, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("probe");
        var sw = Stopwatch.StartNew();
        var result = new StatusCheck
        {
            Environment = environment,
            ServiceName = serviceName,
            CheckType = checkType,
            TimestampUtc = DateTime.UtcNow
        };

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            result.ResponseTimeMs = (int)sw.ElapsedMilliseconds;
            result.HttpStatusCode = (int)response.StatusCode;
            // Treat any non-5xx, non-network-failure response as "up" — a 404 on a UI virtual
            // path still proves the App Service/IIS site is alive and routing, which is what
            // this probe is verifying (not application-level correctness).
            result.IsUp = (int)response.StatusCode < 500;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            result.IsUp = false;
            result.ResponseTimeMs = (int)sw.ElapsedMilliseconds;
            result.ErrorMessage = "Request timed out";
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.IsUp = false;
            result.ResponseTimeMs = (int)sw.ElapsedMilliseconds;
            result.ErrorMessage = Truncate(ex.Message, 1000);
            _logger.LogWarning(ex, "Health check failed for {Service} ({CheckType}) at {Url}", serviceName, checkType, url);
        }

        return result;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
