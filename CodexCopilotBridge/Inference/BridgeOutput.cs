namespace CodexCopilotBridge.Inference;

public abstract record BridgeOutput;

public sealed record TextDeltaOutput(string Delta) : BridgeOutput;

public sealed record FinalTextOutput(string Text) : BridgeOutput;

public sealed record FunctionCallRequestOutput(
    string CallId,
    string Name,
    string Arguments,
    string? Namespace = null) : BridgeOutput;
