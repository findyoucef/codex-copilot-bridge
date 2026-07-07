namespace CodexCopilotBridge;

public sealed record BridgeOptions
{
    public BridgeMode Mode { get; init; } = BridgeMode.Echo;

    public string DefaultModel { get; init; } = "gpt-5.5";

    public string? CopilotHome { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan PendingToolCallTtl { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan PendingToolCallCleanupInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan ToolRequestBatchQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    public int ResponseOutputChannelCapacity { get; init; } = 512;
}
