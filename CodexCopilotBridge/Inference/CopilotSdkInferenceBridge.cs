using CodexCopilotBridge.Protocol;
using GitHub.Copilot;
using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

public sealed class CopilotSdkInferenceBridge(IOptions<BridgeOptions> options, ILogger<CopilotSdkInferenceBridge> logger) : IInferenceBridge
{
    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = CodexPromptRenderer.Render(request);
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Value.DefaultModel : request.Model;

        await foreach (var output in BridgeResponseRunner.RunAsync(options.Value, async (writer, operationToken) =>
        {
            var clientOptions = new CopilotClientOptions
            {
                BaseDirectory = ResolveCopilotHome(),
                UseLoggedInUser = true,
                Logger = logger
            };

            await using var client = new CopilotClient(clientOptions);
            await client.StartAsync(operationToken);

            await using var session = await client.CreateSessionAsync(
                new SessionConfig
                {
                    Model = model,
                    Streaming = true
                },
                operationToken);

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalText = string.Empty;

            void WriteOutput(BridgeOutput bridgeOutput)
            {
                try
                {
                    writer.WriteAsync(bridgeOutput, operationToken).AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }

            session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        var deltaContent = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(deltaContent))
                        {
                            WriteOutput(new TextDeltaOutput(deltaContent));
                        }
                        break;
                    case AssistantMessageEvent message:
                        finalText = message.Data.Content ?? string.Empty;
                        break;
                    case SessionErrorEvent error:
                        done.TrySetException(new InvalidOperationException(error.Data.Message));
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt }, operationToken);
            await done.Task.WaitAsync(operationToken);
            await writer.WriteAsync(new FinalTextOutput(finalText), operationToken);
        }, cancellationToken))
        {
            yield return output;
        }
    }

    private string? ResolveCopilotHome()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.CopilotHome))
        {
            return options.Value.CopilotHome;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? null
            : Path.Combine(home, ".codex-copilot-bridge", "copilot-home");
    }
}
