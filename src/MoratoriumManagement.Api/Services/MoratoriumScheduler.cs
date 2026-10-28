using MoratoriumManagement.Api.Services;

namespace MoratoriumManagement.Api.Services;

/// <summary>FR-05A: promotes Scheduled→Active and expires Active→Inactive as their date/times are reached.</summary>
public sealed class MoratoriumScheduler(IServiceScopeFactory scopes, ILogger<MoratoriumScheduler> log, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Scheduler:IntervalSeconds", 15));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var changed = await scope.ServiceProvider.GetRequiredService<MoratoriumService>().SyncStatusesAsync(stoppingToken);
                if (changed > 0) log.LogInformation("Scheduler transitioned {Count} moratorium(s)", changed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Scheduler run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
