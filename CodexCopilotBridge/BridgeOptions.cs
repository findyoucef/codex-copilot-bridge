namespace CodexCopilotBridge;

public sealed record BridgeOptions
{
    public const int DefaultCodexContextWindow = 272_000;
    public const int DefaultCodexTruncationLimitBytes = 10_000_000;
    public const int DefaultMaxToolOutputChars = 20_000;
    public static readonly TimeSpan DefaultConversationSessionTtl = TimeSpan.FromHours(2);
    public static readonly string[] DefaultExcludedToolNames = ["view_image", "update_plan"];

    public BridgeMode Mode { get; init; } = BridgeMode.Echo;

    public string DefaultModel { get; init; } = "gpt-5.6-sol";

    public string? CopilotHome { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan PendingToolCallTtl { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan PendingToolCallCleanupInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan ConversationSessionTtl { get; init; } = DefaultConversationSessionTtl;

    public TimeSpan ToolRequestBatchQuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    public int ResponseOutputChannelCapacity { get; init; } = 512;

    public int CodexContextWindow { get; init; } = DefaultCodexContextWindow;

    public int CodexTruncationLimitBytes { get; init; } = DefaultCodexTruncationLimitBytes;

    public int MaxToolOutputChars { get; init; } = DefaultMaxToolOutputChars;

    public bool CompactToolSchemas { get; init; } = true;

    public bool EnableConversationSessionReuse { get; init; } = true;

    public string[] ExcludedToolNames { get; init; } = [];

    public string[] ExcludedToolNamePrefixes { get; init; } = [];
}
