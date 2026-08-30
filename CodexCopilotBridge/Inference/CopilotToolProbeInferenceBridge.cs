using System.Text.Json;
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
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Value.DefaultModel : request.Model;
        var reasoningEffort = request.GetReasoningEffort();

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
                    ReasoningEffort = reasoningEffort,
                    Streaming = true,
                    Tools = [CreateBridgeEchoDeclaration()],
                    AvailableTools = [BridgeEchoToolName],
                    SkipCustomInstructions = true,
                    EnableSkills = false,
                    OnPermissionRequest = PermissionHandler.ApproveAll
                },
                operationToken);

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalText = string.Empty;
            var requestedExternalTool = false;
            var hasUsage = false;

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
                    case ExternalToolRequestedEvent externalTool:
                        requestedExternalTool = true;
                        _ = RespondToExternalToolAsync(session, externalTool.Data, operationToken)
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
                    case AssistantUsageEvent usage:
                        var usageOutput = UsageOutput.FromProvider(
                            usage.Data.InputTokens,
                            usage.Data.CacheReadTokens,
                            usage.Data.CacheWriteTokens,
                            usage.Data.OutputTokens,
                            usage.Data.ReasoningTokens);
                        if (usageOutput is null)
                        {
                            logger.LogWarning(
                                "Copilot usage event omitted required token counts. Model={Model} ApiCallId={ApiCallId}",
                                usage.Data.Model,
                                usage.Data.ApiCallId);
                        }
                        else
                        {
                            WriteOutput(usageOutput);
                            hasUsage = true;
                        }
                        break;
                    case SessionUsageInfoEvent contextUsage:
                        logger.LogInformation(
                            "Copilot context usage. Model={Model} CurrentTokens={CurrentTokens} TokenLimit={TokenLimit} ConversationTokens={ConversationTokens} SystemTokens={SystemTokens} ToolDefinitionTokens={ToolDefinitionTokens}",
                            model,
                            contextUsage.Data.CurrentTokens,
                            contextUsage.Data.TokenLimit,
                            contextUsage.Data.ConversationTokens,
                            contextUsage.Data.SystemTokens,
                            contextUsage.Data.ToolDefinitionsTokens);
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
                operationToken);

            await done.Task.WaitAsync(operationToken);

            if (!hasUsage)
            {
                logger.LogWarning("Copilot did not report token usage for tool-probe exchange. Model={Model}", model);
            }

            if (!requestedExternalTool)
            {
                throw new InvalidOperationException($"Copilot did not request the {BridgeEchoToolName} external tool.");
            }

            await writer.WriteAsync(new FinalTextOutput(finalText), operationToken);
        }, cancellationToken))
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
