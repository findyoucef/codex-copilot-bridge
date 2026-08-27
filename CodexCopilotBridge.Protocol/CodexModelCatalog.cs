namespace CodexCopilotBridge.Protocol;

public sealed record ModelsResponse(IReadOnlyList<object> Models);

public static class CodexModelCatalog
{
    public static ModelsResponse CreateDefault(string model)
    {
        var supportedReasoningLevels = new List<object>
        {
            new { Effort = "low", Description = "Low reasoning effort" },
            new { Effort = "medium", Description = "Medium reasoning effort" },
            new { Effort = "high", Description = "High reasoning effort" },
            new { Effort = "xhigh", Description = "Extra high reasoning effort" }
        };

        if (model.StartsWith("gpt-5.6", StringComparison.OrdinalIgnoreCase))
        {
            supportedReasoningLevels.Add(new { Effort = "max", Description = "Maximum reasoning effort" });
            supportedReasoningLevels.Add(new { Effort = "ultra", Description = "Maximum reasoning with automatic task delegation" });
        }

        var modelInfo = new
        {
            Slug = model,
            DisplayName = model,
            Description = "GitHub Copilot model routed through the local Codex Copilot Bridge.",
            DefaultReasoningLevel = "medium",
            SupportedReasoningLevels = supportedReasoningLevels,
            ShellType = "shell_command",
            Visibility = "list",
            SupportedInApi = true,
            Priority = 1,
            Upgrade = (object?)null,
            BaseInstructions = string.Empty,
            SupportsReasoningSummaries = false,
            SupportVerbosity = false,
            DefaultVerbosity = (string?)null,
            ApplyPatchToolType = (string?)null,
            TruncationPolicy = new
            {
                Mode = "bytes",
                Limit = 10_000_000
            },
            SupportsParallelToolCalls = false,
            SupportsImageDetailOriginal = false,
            ContextWindow = 128_000,
            ExperimentalSupportedTools = Array.Empty<string>()
        };

        return new ModelsResponse([modelInfo]);
    }
}
