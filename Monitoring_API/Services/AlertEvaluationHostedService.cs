namespace Monitoring_API.Services;

// Runs alert evaluation on its own, slower cadence — deliberately separate from
// PollingHostedService's every-few-seconds health-check loop, so Claude-call and
// notification-delivery latency never touches the hot health-check path.
public class AlertEvaluationHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<AlertEvaluationHostedService> _logger;

    public AlertEvaluationHostedService(IServiceProvider services, ILogger<AlertEvaluationHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = _services.CreateScope();
                var alerts = scope.ServiceProvider.GetRequiredService<AlertNotificationService>();
                await alerts.EvaluateAndNotifyAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Alert evaluation cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
