namespace CodexCopilotBridge.Protocol;

public sealed record ModelsResponse(IReadOnlyList<object> Models);

public sealed record CodexModelDescriptor(
    string Slug,
    string DisplayName,
    int ContextWindow,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort);

public static class CodexModelCatalog
{
    public static ModelsResponse CreateDefault(
        string model,
        int contextWindow = 272_000,
        int truncationLimitBytes = 10_000_000) =>
        Create(
            [new CodexModelDescriptor(model, model, contextWindow, DefaultReasoningEfforts(model), "medium")],
            truncationLimitBytes);

    public static ModelsResponse Create(
        IReadOnlyList<CodexModelDescriptor> models,
        int truncationLimitBytes = 10_000_000)
    {
        var modelInfos = models
            .Select((model, index) => CreateModelInfo(model, index + 1, truncationLimitBytes))
            .ToArray();

        return new ModelsResponse(modelInfos);
    }

    private static object CreateModelInfo(
        CodexModelDescriptor model,
        int priority,
        int truncationLimitBytes)
    {
        var reasoningEfforts = model.SupportedReasoningEfforts.Count > 0
            ? model.SupportedReasoningEfforts
            : ["medium"];
        var defaultReasoningEffort = reasoningEfforts.Contains(model.DefaultReasoningEffort, StringComparer.Ordinal)
            ? model.DefaultReasoningEffort!
            : reasoningEfforts[0];
        var supportedReasoningLevels = reasoningEfforts
            .Select(effort => new
            {
                Effort = effort,
                Description = ReasoningEffortDescription(effort)
            })
            .ToArray();

        return new
        {
            model.Slug,
            model.DisplayName,
            Description = "GitHub Copilot model routed through the local Codex Copilot Bridge.",
            DefaultReasoningLevel = defaultReasoningEffort,
            SupportedReasoningLevels = supportedReasoningLevels,
            ShellType = "shell_command",
            Visibility = "list",
            SupportedInApi = true,
            Priority = priority,
            Upgrade = (object?)null,
            BaseInstructions = string.Empty,
            SupportsReasoningSummaries = false,
            SupportVerbosity = false,
            DefaultVerbosity = (string?)null,
            ApplyPatchToolType = (string?)null,
            TruncationPolicy = new
            {
                Mode = "bytes",
                Limit = truncationLimitBytes
            },
            SupportsParallelToolCalls = false,
            SupportsImageDetailOriginal = false,
            model.ContextWindow,
            ExperimentalSupportedTools = Array.Empty<string>()
        };
    }

    private static IReadOnlyList<string> DefaultReasoningEfforts(string model) =>
        model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase)
            ? ["low", "medium", "high", "xhigh", "max", "ultra"]
            : ["low", "medium", "high", "xhigh"];

    private static string ReasoningEffortDescription(string effort) =>
        effort switch
        {
            "low" => "Low reasoning effort",
            "medium" => "Medium reasoning effort",
            "high" => "High reasoning effort",
            "xhigh" => "Extra high reasoning effort",
            "max" => "Maximum reasoning effort",
            "ultra" => "Maximum reasoning with automatic task delegation",
            _ => $"{effort} reasoning effort"
        };
}
