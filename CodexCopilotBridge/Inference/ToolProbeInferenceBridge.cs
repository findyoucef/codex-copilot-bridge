using System.Text.Json;
using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge.Inference;

public sealed class ToolProbeInferenceBridge : IInferenceBridge
{
    private const string ProbeCallId = "bridge_probe_shell";

    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var toolOutput = ToolOutputReader.FindTextOutput(request, ProbeCallId);
        if (toolOutput is not null)
        {
            await Task.Yield();
            yield return new FinalTextOutput($"Codex executed the shell tool and returned: {toolOutput.Trim()}");
            yield break;
        }

        var arguments = JsonSerializer.Serialize(
            new
            {
                command = "printf 'codex-tool-probe-ok'",
                timeout_ms = 10_000
            },
            ProtocolJson.SerializerOptions);

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new FunctionCallRequestOutput(ProbeCallId, "shell_command", arguments);
    }
}
