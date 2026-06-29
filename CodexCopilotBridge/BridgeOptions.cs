namespace CodexCopilotBridge;

public sealed record BridgeOptions
{
    public BridgeMode Mode { get; init; } = BridgeMode.Echo;

    public string DefaultModel { get; init; } = "gpt-5";

    public string? CopilotHome { get; init; }
}
