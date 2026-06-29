using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

public sealed record ResponsesApiRequest
{
    public string Model { get; init; } = "gpt-5";

    public string Instructions { get; init; } = string.Empty;

    public IReadOnlyList<JsonElement> Input { get; init; } = [];

    public IReadOnlyList<JsonElement>? Tools { get; init; }

    public string ToolChoice { get; init; } = "auto";

    public bool ParallelToolCalls { get; init; }

    public JsonElement? Reasoning { get; init; }

    public bool Store { get; init; }

    public bool Stream { get; init; } = true;

    public IReadOnlyList<string> Include { get; init; } = [];

    public string? ServiceTier { get; init; }

    public string? PromptCacheKey { get; init; }

    public JsonElement? Text { get; init; }

    public IReadOnlyDictionary<string, string>? ClientMetadata { get; init; }
}
