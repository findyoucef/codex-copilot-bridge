using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge.Inference;

public interface IInferenceBridge
{
    IAsyncEnumerable<BridgeOutput> CreateResponseAsync(ResponsesApiRequest request, CancellationToken cancellationToken);
}
