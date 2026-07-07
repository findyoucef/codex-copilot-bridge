using CodexCopilotBridge;
using CodexCopilotBridge.Inference;
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
            new PendingToolCallStore(NullLogger<PendingToolCallStore>.Instance),
            NullLogger<CopilotToolBridgeInferenceBridge>.Instance);

        await bridge.DisposeAsync();
        await bridge.DisposeAsync();
    }
}
