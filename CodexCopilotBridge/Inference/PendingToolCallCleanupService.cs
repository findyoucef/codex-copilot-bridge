using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

internal sealed class PendingToolCallCleanupService(
    PendingToolCallStore pendingToolCalls,
    ConversationSessionStore conversationSessions,
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
                var expiredConversations = await conversationSessions.ExpireAsync(
                    DateTimeOffset.UtcNow,
                    Positive(options.Value.ConversationSessionTtl, BridgeOptions.DefaultConversationSessionTtl),
                    stoppingToken);

                if (expired > 0)
                {
                    logger.LogInformation("Expired {ExpiredCount} pending Copilot tool call(s).", expired);
                }

                if (expiredConversations > 0)
                {
                    logger.LogInformation("Expired {ExpiredCount} reusable Copilot conversation session(s).", expiredConversations);
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
