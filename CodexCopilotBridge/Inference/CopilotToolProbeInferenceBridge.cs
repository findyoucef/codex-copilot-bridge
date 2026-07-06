using System.Text.Json;
using System.Threading.Channels;
using CodexCopilotBridge.Protocol;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

public sealed class CopilotToolProbeInferenceBridge(
    IOptions<BridgeOptions> options,
    ILogger<CopilotToolProbeInferenceBridge> logger) : IInferenceBridge
{
    private const string BridgeEchoToolName = "bridge_echo";
    private const string BridgeEchoResult = "copilot-external-tool-ok";

    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = BridgeOutputChannels.CreateBounded(options.Value.ResponseOutputChannelCapacity);
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
                        Streaming = true,
                        Tools = [CreateBridgeEchoDeclaration()],
                        AvailableTools = [BridgeEchoToolName],
                        SkipCustomInstructions = true,
                        EnableSkills = false,
                        OnPermissionRequest = PermissionHandler.ApproveAll
                    },
                    cancellationToken);

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finalText = string.Empty;
                var requestedExternalTool = false;

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
	                        case ExternalToolRequestedEvent externalTool:
	                            requestedExternalTool = true;
	                            _ = RespondToExternalToolAsync(session, externalTool.Data, cancellationToken)
	                                .ContinueWith(
	                                    task => done.TrySetException(task.Exception!.GetBaseException()),
	                                    CancellationToken.None,
	                                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
	                                    TaskScheduler.Default);
	                            break;
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

                await session.SendAsync(
                    new MessageOptions
                    {
                        Prompt = $"Call the {BridgeEchoToolName} tool with message \"hello\". Then reply with exactly the tool result and no other text."
                    },
                    cancellationToken);

                await done.Task.WaitAsync(cancellationToken);

                if (!requestedExternalTool)
                {
                    throw new InvalidOperationException($"Copilot did not request the {BridgeEchoToolName} external tool.");
                }

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

    private static AIFunctionDeclaration CreateBridgeEchoDeclaration()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {
                "message": {
                  "type": "string",
                  "description": "Message to echo through the bridge probe."
                }
              },
              "required": ["message"],
              "additionalProperties": false
            }
            """,
            ProtocolJson.SerializerOptions);

        return AIFunctionFactory.CreateDeclaration(
            BridgeEchoToolName,
            "Echoes a message through the Codex Copilot Bridge external-tool probe.",
            schema,
            returnJsonSchema: null);
    }

    private async Task RespondToExternalToolAsync(
        CopilotSession session,
        ExternalToolRequestedData request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ToolName, BridgeEchoToolName, StringComparison.Ordinal))
        {
            await session.Rpc.Tools.HandlePendingToolCallAsync(
                request.RequestId,
                result: null!,
                error: $"Unexpected tool requested: {request.ToolName}",
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Copilot requested external tool {ToolName}. RequestId={RequestId} ToolCallId={ToolCallId} Arguments={Arguments}",
            request.ToolName,
            request.RequestId,
            request.ToolCallId,
            request.Arguments?.ToString());

        await session.Rpc.Tools.HandlePendingToolCallAsync(
            request.RequestId,
            BridgeEchoResult,
            error: null!,
            cancellationToken);
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
