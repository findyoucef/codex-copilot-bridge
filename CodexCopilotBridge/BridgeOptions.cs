namespace CodexCopilotBridge;

public sealed record BridgeOptions
{
    public BridgeMode Mode { get; init; } = BridgeMode.Echo;

    public string DefaultModel { get; init; } = "gpt-5";

    public string? CopilotHome { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PendingToolCallTtl { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PendingToolCallCleanupInterval { get; init; } = TimeSpan.FromMinutes(1);
}
