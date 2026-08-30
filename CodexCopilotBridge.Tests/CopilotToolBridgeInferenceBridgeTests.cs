using CodexCopilotBridge;
using CodexCopilotBridge.Inference;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
            SupportedReasoningEfforts = ["low", "high"],
            DefaultReasoningEffort = "high"
        };

        var descriptor = CopilotToolBridgeInferenceBridge.ToCodexModelDescriptor(model, 32_000);

        Assert.Equal("test-model", descriptor.Slug);
        Assert.Equal("Test Model", descriptor.DisplayName);
        Assert.Equal(32_000, descriptor.ContextWindow);
        Assert.Equal(["low", "high"], descriptor.SupportedReasoningEfforts);
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
}
