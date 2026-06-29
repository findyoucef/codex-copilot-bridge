using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge.Inference;

public sealed class EchoInferenceBridge : IInferenceBridge
{
    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = CodexPromptRenderer.Render(request);
        var response = $"""
        Codex Copilot Bridge is running in Echo mode.

        Requested model: {request.Model}

        Prompt preview:
        {Trim(prompt, 2_000)}
        """;

        foreach (var chunk in Chunk(response, 96))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new TextDeltaOutput(chunk);
        }

        yield return new FinalTextOutput(response);
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var index = 0; index < value.Length; index += size)
        {
            yield return value.Substring(index, Math.Min(size, value.Length - index));
        }
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n...[truncated]";
}
