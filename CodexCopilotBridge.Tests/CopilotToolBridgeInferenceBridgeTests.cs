using CodexCopilotBridge;
using CodexCopilotBridge.Inference;
using CodexCopilotBridge.Protocol;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace CodexCopilotBridge.Tests;

public sealed class CopilotToolBridgeInferenceBridgeTests
{
    [Fact]
    public async Task DisposeAsync_CanBeCalledMoreThanOnce()
    {
        var bridge = new CopilotToolBridgeInferenceBridge(
            Options.Create(new BridgeOptions()),
            new ConversationSessionStore(NullLogger<ConversationSessionStore>.Instance),
            new PendingToolCallStore(NullLogger<PendingToolCallStore>.Instance),
            NullLogger<CopilotToolBridgeInferenceBridge>.Instance);

        await bridge.DisposeAsync();
        await bridge.DisposeAsync();
    }

    [Fact]
    public void ToCodexModelDescriptor_UsesCopilotMetadata()
    {
        var model = new ModelInfo
        {
            Id = "test-model",
            Name = "Test Model",
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits { MaxContextWindowTokens = 123_456 }
            },
            SupportedReasoningEfforts = ["low", "high", "max"],
            DefaultReasoningEffort = "high"
        };

        var descriptor = CopilotToolBridgeInferenceBridge.ToCodexModelDescriptor(model, 32_000);

        Assert.Equal("test-model", descriptor.Slug);
        Assert.Equal("Test Model", descriptor.DisplayName);
        Assert.Equal(32_000, descriptor.ContextWindow);
        Assert.Equal(["low", "high", "max"], descriptor.SupportedReasoningEfforts);
        Assert.Equal("high", descriptor.DefaultReasoningEffort);
    }

    [Fact]
    public void ToCodexModelDescriptor_UsesConfiguredContextFallback()
    {
        var model = new ModelInfo { Id = "test-model" };

        var descriptor = CopilotToolBridgeInferenceBridge.ToCodexModelDescriptor(model, 32_000);

        Assert.Equal("test-model", descriptor.DisplayName);
        Assert.Equal(32_000, descriptor.ContextWindow);
    }

    [Fact]
    public void ToCodexModelDescriptor_PreservesLowerModelContextLimit()
    {
        var model = new ModelInfo
        {
            Id = "test-model",
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits { MaxContextWindowTokens = 16_000 }
            }
        };

        var descriptor = CopilotToolBridgeInferenceBridge.ToCodexModelDescriptor(model, 32_000);

        Assert.Equal(16_000, descriptor.ContextWindow);
    }

    [Fact]
    public void CreateCopilotToolDeclarations_CollapsesNamespaceCatalog()
    {
        var tools = new[]
        {
            Tool("exec_command", "exec_command"),
            Tool("mcp__github__get_issue", "get_issue", "mcp__github__"),
            Tool("mcp__github__list_issues", "list_issues", "mcp__github__")
        };

        var declarations = CopilotToolBridgeInferenceBridge.CreateCopilotToolDeclarations(tools);

        Assert.Equal(
            [
                "exec_command",
                CopilotToolBridgeInferenceBridge.CodexToolSearchName,
                CopilotToolBridgeInferenceBridge.CodexToolInvokeName
            ],
            declarations.Select(tool => tool.Name));
    }

    [Fact]
    public void SearchCodexTools_ReturnsRelevantToolsWithSchemas()
    {
        var tools = new[]
        {
            Tool("mcp__github__get_issue", "get_issue", "mcp__github__", "Get a GitHub issue."),
            Tool("mcp__figma__get_file", "get_file", "mcp__figma__", "Get a Figma file.")
        };

        var json = CopilotToolBridgeInferenceBridge.SearchCodexTools(tools, "GitHub issue");
        using var document = JsonDocument.Parse(json);
        var result = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray());

        Assert.Equal("mcp__github__get_issue", result.GetProperty("name").GetString());
        Assert.Equal("object", result.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void TryResolveCodexToolInvocation_UnwrapsNameAndArguments()
    {
        var expected = Tool("mcp__github__get_issue", "get_issue", "mcp__github__");
        var tools = new Dictionary<string, CodexToolDeclaration>(StringComparer.Ordinal)
        {
            [expected.CopilotName] = expected
        };

        var resolved = CopilotToolBridgeInferenceBridge.TryResolveCodexToolInvocation(
            tools,
            """{"name":"mcp__github__get_issue","arguments":{"issue_number":42}}""",
            out var tool,
            out var arguments,
            out var error);

        Assert.True(resolved, error);
        Assert.Same(expected, tool);
        Assert.Equal("""{"issue_number":42}""", arguments);
    }

    private static CodexToolDeclaration Tool(
        string copilotName,
        string codexName,
        string? codexNamespace = null,
        string description = "Test tool.") =>
        new(
            copilotName,
            codexName,
            codexNamespace,
            description,
            JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","properties":{},"additionalProperties":false}"""));
}
