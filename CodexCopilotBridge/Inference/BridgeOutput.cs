namespace CodexCopilotBridge.Inference;

public abstract record BridgeOutput;

public sealed record TextDeltaOutput(string Delta) : BridgeOutput;

public sealed record FinalTextOutput(string Text) : BridgeOutput;

public sealed record UsageOutput(
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens) : BridgeOutput
{
    public static UsageOutput? FromProvider(
        long? inputTokens,
        long? cachedInputTokens,
        long? cacheWriteInputTokens,
        long? outputTokens,
        long? reasoningOutputTokens) =>
        inputTokens is null || outputTokens is null
            ? null
            : new UsageOutput(
                NonNegative(inputTokens.Value),
                NonNegative(cachedInputTokens),
                NonNegative(cacheWriteInputTokens),
                NonNegative(outputTokens.Value),
                NonNegative(reasoningOutputTokens));

    public UsageOutput Add(UsageOutput other) =>
        new(
            InputTokens + other.InputTokens,
            CachedInputTokens + other.CachedInputTokens,
            CacheWriteInputTokens + other.CacheWriteInputTokens,
            OutputTokens + other.OutputTokens,
            ReasoningOutputTokens + other.ReasoningOutputTokens);

    public object ToResponseUsage() =>
        new
        {
            InputTokens,
            InputTokensDetails = new
            {
                CachedTokens = CachedInputTokens,
                CacheWriteTokens = CacheWriteInputTokens
            },
            OutputTokens,
            OutputTokensDetails = new
            {
                ReasoningTokens = ReasoningOutputTokens
            },
            TotalTokens = InputTokens + OutputTokens
        };

    private static long NonNegative(long? value) => Math.Max(0, value ?? 0);
}

public sealed record FunctionCallRequestOutput(
    string CallId,
    string Name,
    string Arguments,
    string? Namespace = null) : BridgeOutput;
