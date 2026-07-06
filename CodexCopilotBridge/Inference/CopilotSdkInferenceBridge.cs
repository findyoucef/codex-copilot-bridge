using System.Threading.Channels;
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
        var channel = BridgeOutputChannels.CreateBounded(options.Value.ResponseOutputChannelCapacity);
        var prompt = CodexPromptRenderer.Render(request);
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Value.DefaultModel : request.Model;

        _ = Task.Run(async () =>
        {
            try
            {
                var clientOptions = new CopilotClientOptions
                {
                    BaseDirectory = ResolveCopilotHome(),
                    UseLoggedInUser = true,
                    Logger = logger
                };

                await using var client = new CopilotClient(clientOptions);
                await client.StartAsync(cancellationToken);

                await using var session = await client.CreateSessionAsync(
                    new SessionConfig
                    {
                        Model = model,
                        Streaming = true
                    },
                    cancellationToken);

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finalText = string.Empty;

                void WriteOutput(BridgeOutput output)
                {
                    try
                    {
                        channel.Writer.WriteAsync(output, cancellationToken).AsTask().GetAwaiter().GetResult();
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

                await session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);
                await done.Task.WaitAsync(cancellationToken);
                await channel.Writer.WriteAsync(new FinalTextOutput(finalText), cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var output in channel.Reader.ReadAllAsync(cancellationToken))
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
