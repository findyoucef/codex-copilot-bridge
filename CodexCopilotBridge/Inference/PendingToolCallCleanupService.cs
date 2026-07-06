using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

internal sealed class PendingToolCallCleanupService(
    PendingToolCallStore pendingToolCalls,
    IOptions<BridgeOptions> options,
    ILogger<PendingToolCallCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = Positive(options.Value.PendingToolCallCleanupInterval, TimeSpan.FromMinutes(1));
            try
            {
                await Task.Delay(interval, stoppingToken);
                var expired = await pendingToolCalls.ExpireAsync(
                    DateTimeOffset.UtcNow,
                    Positive(options.Value.PendingToolCallTtl, TimeSpan.FromHours(1)),
                    stoppingToken);

                if (expired > 0)
                {
                    logger.LogInformation("Expired {ExpiredCount} pending Copilot tool call(s).", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}
