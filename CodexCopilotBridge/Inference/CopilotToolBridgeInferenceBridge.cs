using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexCopilotBridge.Protocol;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

internal sealed class CopilotToolBridgeInferenceBridge(
    IOptions<BridgeOptions> options,
    ConversationSessionStore conversationSessions,
    PendingToolCallStore pendingToolCalls,
    ILogger<CopilotToolBridgeInferenceBridge> logger) : IInferenceBridge, IAsyncDisposable
{
    private static readonly TimeSpan DefaultToolRequestBatchQuietPeriod = TimeSpan.FromMilliseconds(500);
    internal const string CodexToolSearchName = "codex_copilot_bridge_search_tools";
    internal const string CodexToolInvokeName = "codex_copilot_bridge_invoke_tool";
    private const int MaxToolSearchResults = 10;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationKeyGates = new(StringComparer.Ordinal);
    private CopilotClient? _client;
    private int _disposed;

    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var output in BridgeResponseRunner.RunAsync(options.Value, async (writer, operationToken) =>
        {
            var bridgeOptions = options.Value;
            var maxToolOutputChars = PositiveInt(bridgeOptions.MaxToolOutputChars, BridgeOptions.DefaultMaxToolOutputChars);
            var outputMatch = pendingToolCalls.FindOutputs(request, out var pendingToolOutputs, maxToolOutputChars);
            if (outputMatch == PendingToolOutputMatchKind.Pending)
            {
                await ContinuePendingToolCallsAsync(request, pendingToolOutputs, writer, operationToken);
            }
            else
            {
                if (outputMatch == PendingToolOutputMatchKind.Retired)
                {
                    logger.LogInformation("Ignoring stale Codex tool output for a completed or retired Copilot tool call.");
                }

                await StartCopilotTurnAsync(request, writer, operationToken);
            }
        }, cancellationToken))
        {
            yield return output;
        }
    }

    public async Task<ModelsResponse> ListModelsAsync(CancellationToken cancellationToken)
    {
        var bridgeOptions = options.Value;
        var client = await GetCopilotClientAsync(cancellationToken);
        var availableModels = await client.ListModelsAsync(cancellationToken);
        var defaultContextWindow = PositiveInt(
            bridgeOptions.CodexContextWindow,
            BridgeOptions.DefaultCodexContextWindow);
        var models = availableModels
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .OrderByDescending(model => string.Equals(model.Id, bridgeOptions.DefaultModel, StringComparison.Ordinal))
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(model => ToCodexModelDescriptor(model, defaultContextWindow))
            .ToArray();

        if (models.Length == 0)
        {
            throw new InvalidOperationException("Copilot returned no available models.");
        }

        return CodexModelCatalog.Create(
            models,
            PositiveInt(
                bridgeOptions.CodexTruncationLimitBytes,
                BridgeOptions.DefaultCodexTruncationLimitBytes));
    }

    internal static CodexModelDescriptor ToCodexModelDescriptor(ModelInfo model, int defaultContextWindow)
    {
        var reasoningEfforts = (model.SupportedReasoningEfforts ?? [])
            .Where(effort => !string.IsNullOrWhiteSpace(effort))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var reportedContextWindow = model.Capabilities.Limits.MaxContextWindowTokens;
        var contextWindow = reportedContextWindow > 0
            ? Math.Min(reportedContextWindow, defaultContextWindow)
            : defaultContextWindow;

        return new CodexModelDescriptor(
            model.Id,
            string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
            contextWindow,
            reasoningEfforts,
            model.DefaultReasoningEffort);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await pendingToolCalls.DisposeAsync();
        await conversationSessions.DisposeAsync();

        await _clientGate.WaitAsync();
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }
        finally
        {
            _clientGate.Release();
            _clientGate.Dispose();
            foreach (var gate in _conversationKeyGates.Values)
            {
                gate.Dispose();
            }
        }
    }

    private async Task StartCopilotTurnAsync(
        ResponsesApiRequest request,
        ChannelWriter<BridgeOutput> writer,
        CancellationToken cancellationToken)
    {
        var bridgeOptions = options.Value;
        var maxToolOutputChars = PositiveInt(bridgeOptions.MaxToolOutputChars, BridgeOptions.DefaultMaxToolOutputChars);
        var tools = CodexToolReader.ReadToolDeclarations(
            request,
            new CodexToolReaderOptions
            {
                CompactSchemas = bridgeOptions.CompactToolSchemas,
                ExcludedToolNames = new HashSet<string>(
                    EffectiveExcludedToolNames(bridgeOptions.ExcludedToolNames),
                    StringComparer.Ordinal),
                ExcludedToolNamePrefixes = bridgeOptions.ExcludedToolNamePrefixes ?? []
            });
        var model = ResolveModel(request);
        var reasoningEffort = request.GetReasoningEffort();
        var toolSignature = CreateToolSignature(tools);
        var conversationKey = bridgeOptions.EnableConversationSessionReuse
            ? ResolveConversationKey(request)
            : null;
        SemaphoreSlim? conversationKeyGate = null;
        var conversationKeyGateHeld = false;
        if (!string.IsNullOrWhiteSpace(conversationKey))
        {
            conversationKeyGate = _conversationKeyGates.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
            await conversationKeyGate.WaitAsync(cancellationToken);
            conversationKeyGateHeld = true;
        }

        var exchange = new BridgeExchange(writer, cancellationToken);
        CopilotBridgeState? state = null;
        var stateReserved = false;
        var disposeState = false;
        var gateHeld = false;
        var commonPrefix = 0;
        var sendFullPrompt = true;
        var prompt = string.Empty;

        try
        {
            var resolvedState = await GetOrCreateCopilotStateAsync(
                conversationKey,
                model,
                reasoningEffort,
                tools,
                toolSignature,
                request.Instructions,
                cancellationToken);
            state = resolvedState.State;
            stateReserved = resolvedState.Reserved;

            await state.Gate.WaitAsync(cancellationToken);
            gateHeld = true;

            if (state.WaitingForToolOutput)
            {
                logger.LogInformation(
                    "Restarting Copilot session because it was waiting for tool output from a cancelled or compacted turn. ConversationKey={ConversationKey}",
                    conversationKey);
                await RestartStateAsync();
            }

            if (!state.CanReuse(model, reasoningEffort, toolSignature, request.Instructions) &&
                !string.IsNullOrWhiteSpace(conversationKey))
            {
                logger.LogInformation(
                    "Replacing reusable Copilot session because request compatibility changed. ConversationKey={ConversationKey}",
                    conversationKey);
                await RestartStateAsync();
            }

            commonPrefix = state.CountCommonInputPrefix(request);
            if (commonPrefix < state.InputFingerprintCount &&
                !string.IsNullOrWhiteSpace(conversationKey))
            {
                logger.LogInformation(
                    "Restarting reusable Copilot session because request history diverged. ConversationKey={ConversationKey}",
                    conversationKey);
                await RestartStateAsync();
            }

            sendFullPrompt = commonPrefix == 0 || state.InputFingerprintCount == 0;
            prompt = sendFullPrompt
                ? CodexPromptRenderer.Render(request, includeToolNotice: false, maxToolOutputChars)
                : CodexPromptRenderer.RenderDelta(request, commonPrefix, maxToolOutputChars);

            if (!sendFullPrompt && string.IsNullOrWhiteSpace(prompt))
            {
                logger.LogInformation(
                    "Reusable Copilot session delta was empty; restarting session with full prompt. ConversationKey={ConversationKey}",
                    conversationKey);
                await RestartStateAsync();
                commonPrefix = 0;
                sendFullPrompt = true;
                prompt = CodexPromptRenderer.Render(request, includeToolNotice: false, maxToolOutputChars);
            }

            if (conversationKeyGateHeld)
            {
                conversationKeyGate!.Release();
                conversationKeyGateHeld = false;
            }

            state.ResetFinalText();
            state.OpenExchange(exchange);

            logger.LogInformation(
                "Sending Copilot bridge prompt. ConversationKey={ConversationKey} Reused={Reused} CommonInputItems={CommonInputItems} TotalInputItems={TotalInputItems} PromptChars={PromptChars} ToolDeclarations={ToolCount}",
                conversationKey,
                !sendFullPrompt,
                commonPrefix,
                request.Input.Count,
                prompt.Length,
                tools.Count);

            await state.Session.SendAsync(
                new MessageOptions
                {
                    Prompt = prompt
                },
                cancellationToken);

            state.UpdateRequestCursor(request);
            await exchange.Done.Task.WaitAsync(cancellationToken);

            if (!exchange.HasUsage)
            {
                logger.LogWarning(
                    "Copilot did not report token usage for bridge exchange. Model={Model} ConversationKey={ConversationKey}",
                    state.Model,
                    string.IsNullOrWhiteSpace(state.ConversationKey) ? null : state.ConversationKey);
            }

            if (!state.WaitingForToolOutput)
            {
                await writer.WriteAsync(new FinalTextOutput(state.FinalText), cancellationToken);
                disposeState = string.IsNullOrWhiteSpace(conversationKey);
                state.Touch(DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            disposeState = gateHeld || string.IsNullOrWhiteSpace(conversationKey);
            throw;
        }
        finally
        {
            ValueTask disposal = default;
            if (disposeState && state is not null)
            {
                if (!string.IsNullOrWhiteSpace(state.ConversationKey))
                {
                    state.MarkFaulted();
                }

                conversationSessions.RemoveForSession(state);
                pendingToolCalls.RemoveForSession(state);
                disposal = state.DisposeAsync();
            }

            if (gateHeld)
            {
                state?.CloseExchange(exchange);
                state?.Gate.Release();
            }

            if (conversationKeyGateHeld)
            {
                conversationKeyGate!.Release();
            }

            exchange.Dispose();

            if (disposeState && state is not null)
            {
                if (stateReserved)
                {
                    state.ReleaseReservation();
                    stateReserved = false;
                }

                await disposal;
            }
            else if (stateReserved)
            {
                state?.ReleaseReservation();
            }
        }

        async Task RestartStateAsync()
        {
            var previousState = state!;
            conversationSessions.RemoveForSession(previousState);
            pendingToolCalls.RemoveForSession(previousState);
            var disposal = previousState.DisposeAsync();

            previousState.Gate.Release();
            gateHeld = false;
            if (stateReserved)
            {
                previousState.ReleaseReservation();
                stateReserved = false;
            }

            await disposal;

            state = await CreateCopilotStateAsync(
                model,
                reasoningEffort,
                tools,
                conversationKey ?? string.Empty,
                request.Instructions,
                toolSignature,
                cancellationToken);
            if (!state.TryReserve())
            {
                throw new InvalidOperationException("Could not reserve a newly created Copilot bridge session.");
            }

            stateReserved = true;
            if (!string.IsNullOrWhiteSpace(conversationKey))
            {
                await conversationSessions.AddOrReplaceAsync(state);
            }

            await state.Gate.WaitAsync(cancellationToken);
            gateHeld = true;
            commonPrefix = 0;
        }
    }

    private async Task ContinuePendingToolCallsAsync(
        ResponsesApiRequest request,
        IReadOnlyList<PendingToolOutput> pendingToolOutputs,
        ChannelWriter<BridgeOutput> writer,
        CancellationToken cancellationToken)
    {
        var pending = pendingToolOutputs[0].PendingCall;
        if (pending.Session is not CopilotBridgeState state)
        {
            throw new InvalidOperationException($"Pending Copilot tool call {pending.CallId} is not attached to a bridge session.");
        }

        if (pendingToolOutputs.Any(output => !ReferenceEquals(output.PendingCall.Session, state)))
        {
            throw new InvalidOperationException("Matched Codex tool outputs belong to multiple Copilot bridge sessions.");
        }

        if (!state.TryReserve())
        {
            return;
        }

        var exchange = new BridgeExchange(writer, cancellationToken);
        var disposeState = false;
        var gateHeld = false;

        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            gateHeld = true;
            var currentOutputs = pendingToolOutputs
                .Where(output => pendingToolCalls.ContainsCallId(output.PendingCall.CallId))
                .ToArray();
            if (currentOutputs.Length == 0)
            {
                return;
            }

            state.ResetFinalText();
            state.OpenExchange(exchange);

            foreach (var pendingToolOutput in currentOutputs)
            {
                pending = pendingToolOutput.PendingCall;
                logger.LogInformation(
                    "Returning Codex tool output to Copilot. CallId={CallId} Tool={ToolName} OutputChars={OutputChars}",
                    pending.CallId,
                    pending.Tool.CopilotName,
                    pendingToolOutput.Output.Length);

                await state.Session.Rpc.Tools.HandlePendingToolCallAsync(
                    pending.CopilotRequestId,
                    pendingToolOutput.Output,
                    error: null!,
                    cancellationToken);

                if (pendingToolCalls.TryRemove(pending))
                {
                    state.MarkToolOutputSubmitted(pending);
                }
            }

            state.UpdateRequestCursor(request);
            if (state.WaitingForToolOutput)
            {
                return;
            }

            await exchange.Done.Task.WaitAsync(cancellationToken);

            if (!exchange.HasUsage)
            {
                logger.LogWarning(
                    "Copilot did not report token usage for tool-output exchange. Model={Model} ConversationKey={ConversationKey}",
                    state.Model,
                    string.IsNullOrWhiteSpace(state.ConversationKey) ? null : state.ConversationKey);
            }

            if (!state.WaitingForToolOutput)
            {
                await writer.WriteAsync(new FinalTextOutput(state.FinalText), cancellationToken);
                disposeState = string.IsNullOrWhiteSpace(state.ConversationKey);
                state.Touch(DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            disposeState = gateHeld;
            throw;
        }
        finally
        {
            ValueTask disposal = default;
            if (disposeState)
            {
                conversationSessions.RemoveForSession(state);
                pendingToolCalls.RemoveForSession(state);
                disposal = state.DisposeAsync();
            }

            if (gateHeld)
            {
                state.CloseExchange(exchange);
                state.Gate.Release();
            }

            exchange.Dispose();

            if (disposeState)
            {
                state.ReleaseReservation();
                await disposal;
            }
            else
            {
                state.ReleaseReservation();
            }
        }
    }

    private async Task<CopilotBridgeState> CreateCopilotStateAsync(
        string model,
        string? reasoningEffort,
        IReadOnlyList<CodexToolDeclaration> tools,
        string conversationKey,
        string instructionsFingerprint,
        string toolSignature,
        CancellationToken cancellationToken)
    {
        var client = await GetCopilotClientAsync(cancellationToken);

        var config = new SessionConfig
        {
            Model = model,
            ReasoningEffort = reasoningEffort,
            Streaming = true,
            SkipCustomInstructions = true,
            EnableSkills = false,
            OnPermissionRequest = PermissionHandler.ApproveAll
        };

        var copilotTools = CreateCopilotToolDeclarations(tools);
        if (copilotTools.Count > 0)
        {
            config.Tools = copilotTools;
            config.AvailableTools = copilotTools.Select(tool => tool.Name).ToList();
        }

        var session = await client.CreateSessionAsync(config, cancellationToken);
        var state = new CopilotBridgeState(
            session,
            tools,
            model,
            reasoningEffort,
            conversationKey,
            instructionsFingerprint,
            toolSignature);
        session.On<SessionEvent>(evt => HandleSessionEvent(state, evt));

        logger.LogInformation(
            "Started Copilot bridge session with {CodexToolCount} Codex and {CopilotToolCount} Copilot tool declaration(s). ConversationKey={ConversationKey} ReasoningEffort={ReasoningEffort}",
            tools.Count,
            copilotTools.Count,
            string.IsNullOrWhiteSpace(conversationKey) ? null : conversationKey,
            reasoningEffort);
        return state;
    }

    private async Task<ResolvedCopilotState> GetOrCreateCopilotStateAsync(
        string? conversationKey,
        string model,
        string? reasoningEffort,
        IReadOnlyList<CodexToolDeclaration> tools,
        string toolSignature,
        string instructionsFingerprint,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(conversationKey) &&
            conversationSessions.TryGet(conversationKey, out var reusableSession) &&
            reusableSession is CopilotBridgeState reusableState)
        {
            return new ResolvedCopilotState(reusableState, Reserved: true);
        }

        var state = await CreateCopilotStateAsync(
            model,
            reasoningEffort,
            tools,
            conversationKey ?? string.Empty,
            instructionsFingerprint,
            toolSignature,
            cancellationToken);
        if (!state.TryReserve())
        {
            throw new InvalidOperationException("Could not reserve a newly created Copilot bridge session.");
        }

        if (!string.IsNullOrWhiteSpace(conversationKey))
        {
            await conversationSessions.AddOrReplaceAsync(state);
        }

        return new ResolvedCopilotState(state, Reserved: true);
    }

    private sealed record ResolvedCopilotState(CopilotBridgeState State, bool Reserved);

    private string ResolveModel(ResponsesApiRequest request) =>
        string.IsNullOrWhiteSpace(request.Model) ? options.Value.DefaultModel : request.Model;

    private static string? ResolveConversationKey(ResponsesApiRequest request)
    {
        if (TryGetMetadataValue(request, "thread_id", out var threadId))
        {
            return $"thread:{threadId}";
        }

        if (TryGetMetadataValue(request, "session_id", out var sessionId))
        {
            return $"session:{sessionId}";
        }

        if (!string.IsNullOrWhiteSpace(request.PromptCacheKey))
        {
            return $"prompt_cache:{request.PromptCacheKey}";
        }

        if (TryGetMetadataValue(request, "x-codex-window-id", out var windowId))
        {
            return $"window:{windowId}";
        }

        return null;
    }

    private static bool TryGetMetadataValue(ResponsesApiRequest request, string key, out string value)
    {
        value = string.Empty;
        if (request.ClientMetadata is null ||
            !request.ClientMetadata.TryGetValue(key, out var metadataValue) ||
            string.IsNullOrWhiteSpace(metadataValue))
        {
            return false;
        }

        value = metadataValue;
        return true;
    }

    private static string CreateToolSignature(IReadOnlyList<CodexToolDeclaration> tools)
    {
        var builder = new StringBuilder();
        foreach (var tool in tools.OrderBy(tool => tool.CopilotName, StringComparer.Ordinal))
        {
            builder.Append(tool.CopilotName);
            builder.Append('\u001f');
            builder.Append(tool.CodexName);
            builder.Append('\u001f');
            builder.Append(tool.CodexNamespace);
            builder.Append('\u001f');
            builder.Append(tool.Description);
            builder.Append('\u001f');
            builder.Append(tool.Parameters.GetRawText());
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private async Task<CopilotClient> GetCopilotClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _clientGate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var clientOptions = new CopilotClientOptions
            {
                BaseDirectory = ResolveCopilotHome(),
                UseLoggedInUser = true,
                Logger = logger
            };

            var client = new CopilotClient(clientOptions);
            try
            {
                await client.StartAsync(cancellationToken);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            _client = client;
            return client;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private void HandleSessionEvent(CopilotBridgeState state, SessionEvent evt)
    {
        switch (evt)
        {
            case ExternalToolRequestedEvent externalTool:
                HandleExternalToolRequest(state, externalTool.Data);
                break;
            case AssistantMessageDeltaEvent delta:
                var deltaContent = delta.Data.DeltaContent;
                if (!string.IsNullOrEmpty(deltaContent))
                {
                    state.CurrentExchange?.Write(new TextDeltaOutput(deltaContent));
                }
                break;
            case AssistantMessageEvent message:
                state.FinalText = message.Data.Content ?? string.Empty;
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
                else if (state.CurrentExchange is { } exchange)
                {
                    exchange.Write(usageOutput);
                }
                else
                {
                    logger.LogWarning(
                        "Copilot reported token usage without an active Codex exchange. Model={Model} ApiCallId={ApiCallId}",
                        usage.Data.Model,
                        usage.Data.ApiCallId);
                }
                break;
            case SessionUsageInfoEvent contextUsage:
                logger.LogInformation(
                    "Copilot context usage. Model={Model} CurrentTokens={CurrentTokens} TokenLimit={TokenLimit} ConversationTokens={ConversationTokens} SystemTokens={SystemTokens} ToolDefinitionTokens={ToolDefinitionTokens}",
                    state.Model,
                    contextUsage.Data.CurrentTokens,
                    contextUsage.Data.TokenLimit,
                    contextUsage.Data.ConversationTokens,
                    contextUsage.Data.SystemTokens,
                    contextUsage.Data.ToolDefinitionsTokens);
                break;
            case SessionErrorEvent error:
                state.CurrentExchange?.Done.TrySetException(new InvalidOperationException(error.Data.Message));
                break;
            case SessionIdleEvent:
                state.CurrentExchange?.Complete();
                break;
        }
    }

    private void HandleExternalToolRequest(CopilotBridgeState state, ExternalToolRequestedData request)
    {
        var exchange = state.CurrentExchange;
        if (exchange is null)
        {
            logger.LogWarning("Copilot requested external tool {ToolName} without an active Codex exchange.", request.ToolName);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            exchange.Done.TrySetException(new InvalidOperationException("Copilot requested an external tool without a tool name."));
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            exchange.Done.TrySetException(new InvalidOperationException("Copilot requested an external tool without a request id."));
            return;
        }

        if (string.Equals(request.ToolName, CodexToolSearchName, StringComparison.Ordinal))
        {
            // The SDK executes this registered handler; its request event is also broadcast here.
            return;
        }

        CodexToolDeclaration tool;
        var arguments = RenderArguments(request);
        if (string.Equals(request.ToolName, CodexToolInvokeName, StringComparison.Ordinal))
        {
            if (!TryResolveCodexToolInvocation(state.ToolsByCopilotName, arguments, out tool, out arguments, out var error))
            {
                exchange.Done.TrySetException(new InvalidOperationException(error));
                return;
            }
        }
        else if (!state.ToolsByCopilotName.TryGetValue(request.ToolName, out tool!))
        {
            exchange.Done.TrySetException(new InvalidOperationException($"Copilot requested unknown external tool {request.ToolName}."));
            return;
        }

        var callId = CreateUniqueCallId(request);
        var pending = new PendingToolCall(callId, request.RequestId, state, tool, DateTimeOffset.UtcNow);
        state.MarkWaitingForToolOutput(pending);

        if (!pendingToolCalls.TryAdd(pending))
        {
            exchange.Done.TrySetException(new InvalidOperationException($"Could not register pending Copilot tool call {callId}."));
            return;
        }

        logger.LogInformation(
            "Copilot requested Codex tool. CallId={CallId} Tool={ToolName}",
            callId,
            tool.CopilotName);

        exchange.Write(new FunctionCallRequestOutput(
            callId,
            tool.CodexName,
            arguments,
            tool.CodexNamespace));
        exchange.CompleteAfterQuietPeriod(Positive(options.Value.ToolRequestBatchQuietPeriod, DefaultToolRequestBatchQuietPeriod));
    }

    internal static List<AIFunctionDeclaration> CreateCopilotToolDeclarations(
        IReadOnlyList<CodexToolDeclaration> tools)
    {
        var declarations = tools
            .Where(tool => string.IsNullOrWhiteSpace(tool.CodexNamespace))
            .Select(CreateCopilotToolDeclaration)
            .ToList();
        var namespacedTools = tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.CodexNamespace))
            .ToArray();
        if (namespacedTools.Length == 0)
        {
            return declarations;
        }

        declarations.Add(AIFunctionFactory.Create(
            (Func<string, string>)(query => SearchCodexTools(namespacedTools, query)),
            new AIFunctionFactoryOptions
            {
                Name = CodexToolSearchName,
                Description = "Search namespaced Codex tools by service and operation. Returns exact tool names, descriptions, and parameter schemas. Search before calling the invoke tool."
            }));
        declarations.Add(AIFunctionFactory.CreateDeclaration(
            CodexToolInvokeName,
            "Invoke a namespaced Codex tool using an exact name and arguments returned by the search tool.",
            JsonSerializer.Deserialize<JsonElement>(
                """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" },
                    "arguments": { "type": "object", "additionalProperties": true }
                  },
                  "required": ["name", "arguments"],
                  "additionalProperties": false
                }
                """,
                ProtocolJson.SerializerOptions),
            returnJsonSchema: null));
        return declarations;
    }

    internal static string SearchCodexTools(
        IReadOnlyList<CodexToolDeclaration> tools,
        string query)
    {
        var normalizedQuery = NormalizeSearchText(query);
        var terms = normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matches = tools
            .Select(tool => new { Tool = tool, Score = ScoreToolMatch(tool, normalizedQuery, terms) })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Tool.CopilotName, StringComparer.Ordinal)
            .Take(MaxToolSearchResults)
            .Select(match => new
            {
                name = match.Tool.CopilotName,
                description = match.Tool.Description,
                parameters = match.Tool.Parameters
            });

        return JsonSerializer.Serialize(new { tools = matches }, ProtocolJson.SerializerOptions);
    }

    internal static bool TryResolveCodexToolInvocation(
        IReadOnlyDictionary<string, CodexToolDeclaration> toolsByCopilotName,
        string invocationJson,
        out CodexToolDeclaration tool,
        out string argumentsJson,
        out string error)
    {
        tool = null!;
        argumentsJson = "{}";
        error = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(invocationJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElement.GetString()))
            {
                error = $"Copilot called {CodexToolInvokeName} without a valid tool name.";
                return false;
            }

            var name = nameElement.GetString()!;
            if (!toolsByCopilotName.TryGetValue(name, out tool!))
            {
                error = $"Copilot requested unknown Codex tool {name}.";
                return false;
            }

            if (!root.TryGetProperty("arguments", out var argumentsElement) ||
                argumentsElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Copilot called {CodexToolInvokeName} without an arguments object.";
                return false;
            }

            argumentsJson = argumentsElement.GetRawText();
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Copilot called {CodexToolInvokeName} with invalid JSON: {exception.Message}";
            return false;
        }
    }

    private static AIFunctionDeclaration CreateCopilotToolDeclaration(CodexToolDeclaration tool) =>
        AIFunctionFactory.CreateDeclaration(
            tool.CopilotName,
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"Execute the Codex {tool.CodexName} tool."
                : tool.Description,
            tool.Parameters,
            returnJsonSchema: null);

    private static int ScoreToolMatch(
        CodexToolDeclaration tool,
        string normalizedQuery,
        IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return 0;
        }

        var name = NormalizeSearchText(tool.CodexName);
        var description = NormalizeSearchText(tool.Description);
        var toolNamespace = NormalizeSearchText(tool.CodexNamespace ?? string.Empty);
        var score = string.Equals(name, normalizedQuery, StringComparison.Ordinal) ? 1000 : 0;
        if (name.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 120;
        }
        if (description.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 40;
        }

        foreach (var term in terms)
        {
            if (name.Contains(term, StringComparison.Ordinal))
            {
                score += 20;
            }
            if (description.Contains(term, StringComparison.Ordinal))
            {
                score += 5;
            }
            if (toolNamespace.Contains(term, StringComparison.Ordinal))
            {
                score += 2;
            }
        }

        return score;
    }

    private static string NormalizeSearchText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : ' ');
        }

        return builder.ToString();
    }

    private static string RenderArguments(ExternalToolRequestedData request)
    {
        var arguments = request.Arguments?.ToString();
        return string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
    }

    private string CreateUniqueCallId(ExternalToolRequestedData request)
    {
        var seed = !string.IsNullOrWhiteSpace(request.ToolCallId)
            ? request.ToolCallId
            : request.RequestId ?? Guid.NewGuid().ToString("N");
        var callId = $"copilot_{SanitizeCallId(seed)}";
        if (!pendingToolCalls.ContainsCallId(callId))
        {
            return callId;
        }

        return $"copilot_{Guid.NewGuid():N}";
    }

    private static string SanitizeCallId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? Guid.NewGuid().ToString("N") : builder.ToString();
    }

    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;

    private static int PositiveInt(int value, int fallback) =>
        value > 0 ? value : fallback;

    private static IReadOnlyList<string> EffectiveExcludedToolNames(string[]? excludedToolNames) =>
        excludedToolNames is { Length: > 0 }
            ? excludedToolNames
            : BridgeOptions.DefaultExcludedToolNames;

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

    private sealed class BridgeExchange(ChannelWriter<BridgeOutput> writer, CancellationToken cancellationToken) : IDisposable
    {
        private readonly object _sync = new();
        private CancellationTokenSource? _quietPeriod;
        private int _hasUsage;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasUsage => Volatile.Read(ref _hasUsage) != 0;

        public void Write(BridgeOutput output)
        {
            try
            {
                writer.WriteAsync(output, cancellationToken).AsTask().GetAwaiter().GetResult();
                if (output is UsageOutput)
                {
                    Volatile.Write(ref _hasUsage, 1);
                }
            }
            catch (Exception ex)
            {
                Done.TrySetException(ex);
            }
        }

        public void Complete()
        {
            Done.TrySetResult();
            CancelQuietPeriod();
        }

        public void CompleteAfterQuietPeriod(TimeSpan quietPeriod)
        {
            CancellationTokenSource quietPeriodToken;
            CancellationTokenSource? previous;

            lock (_sync)
            {
                if (Done.Task.IsCompleted)
                {
                    return;
                }

                previous = _quietPeriod;
                quietPeriodToken = new CancellationTokenSource();
                _quietPeriod = quietPeriodToken;
            }

            previous?.Cancel();

            _ = CompleteAfterQuietPeriodAsync(quietPeriod, quietPeriodToken);
        }

        public void Dispose()
        {
            CancelQuietPeriod();
        }

        private async Task CompleteAfterQuietPeriodAsync(TimeSpan quietPeriod, CancellationTokenSource quietPeriodToken)
        {
            try
            {
                await Task.Delay(quietPeriod, quietPeriodToken.Token);
                Done.TrySetResult();
            }
            catch (OperationCanceledException) when (quietPeriodToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_quietPeriod, quietPeriodToken))
                    {
                        _quietPeriod = null;
                    }
                }

                quietPeriodToken.Dispose();
            }
        }

        private void CancelQuietPeriod()
        {
            CancellationTokenSource? quietPeriod;
            lock (_sync)
            {
                quietPeriod = _quietPeriod;
                _quietPeriod = null;
            }

            quietPeriod?.Cancel();
        }
    }

    private sealed class CopilotBridgeState(
        CopilotSession session,
        IReadOnlyList<CodexToolDeclaration> tools,
        string model,
        string? reasoningEffort,
        string conversationKey,
        string instructionsFingerprint,
        string toolSignature) : IReusableBridgeSession
    {
        private readonly object _sync = new();
        private readonly BridgeSessionLifetime _lifetime = new(session.DisposeAsync);
        private bool _faulted;
        private readonly HashSet<string> _pendingToolCallIds = new(StringComparer.Ordinal);
        private IReadOnlyList<string> _inputFingerprints = [];
        private long _lastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;

        public CopilotSession Session { get; } = session;

        public string Model { get; } = model;

        public string? ReasoningEffort { get; } = reasoningEffort;

        public string ConversationKey { get; } = conversationKey;

        public string InstructionsFingerprint { get; } = instructionsFingerprint;

        public string ToolSignature { get; } = toolSignature;

        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyDictionary<string, CodexToolDeclaration> ToolsByCopilotName { get; } =
            tools.ToDictionary(tool => tool.CopilotName, StringComparer.Ordinal);

        public BridgeExchange? CurrentExchange { get; private set; }

        public string FinalText { get; set; } = string.Empty;

        public int InputFingerprintCount
        {
            get
            {
                lock (_sync)
                {
                    return _inputFingerprints.Count;
                }
            }
        }

        public bool WaitingForToolOutput
        {
            get
            {
                lock (_sync)
                {
                    return _pendingToolCallIds.Count > 0;
                }
            }
        }

        public bool IsActive
        {
            get
            {
                lock (_sync)
                {
                    return CurrentExchange is not null || _lifetime.IsActive;
                }
            }
        }

        public bool TryReserve() => _lifetime.TryReserve();

        public void ReleaseReservation() => _lifetime.ReleaseReservation();

        public bool TryRetireIfIdle()
        {
            lock (_sync)
            {
                if (_pendingToolCallIds.Count > 0 || CurrentExchange is not null)
                {
                    return false;
                }

                return _lifetime.TryRetireIfIdle();
            }
        }

        public bool TryRetireForPendingToolExpiry()
        {
            lock (_sync)
            {
                if (CurrentExchange is not null)
                {
                    return false;
                }

                return _lifetime.TryRetireIfIdle();
            }
        }

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

        public bool CanReuse(
            string model,
            string? reasoningEffort,
            string toolSignature,
            string instructionsFingerprint)
        {
            lock (_sync)
            {
                return !_lifetime.IsRetired &&
                    !_faulted &&
                    string.Equals(Model, model, StringComparison.Ordinal) &&
                    string.Equals(ReasoningEffort, reasoningEffort, StringComparison.Ordinal) &&
                    string.Equals(ToolSignature, toolSignature, StringComparison.Ordinal) &&
                    string.Equals(InstructionsFingerprint, instructionsFingerprint, StringComparison.Ordinal);
            }
        }

        public void MarkFaulted()
        {
            lock (_sync)
            {
                _faulted = true;
            }
        }

        public int CountCommonInputPrefix(ResponsesApiRequest request)
        {
            lock (_sync)
            {
                return CodexPromptRenderer.CountCommonInputPrefix(_inputFingerprints, request);
            }
        }

        public void UpdateRequestCursor(ResponsesApiRequest request)
        {
            lock (_sync)
            {
                _inputFingerprints = CodexPromptRenderer.CreateInputFingerprints(request);
            }

            Touch(DateTimeOffset.UtcNow);
        }

        public void ResetFinalText()
        {
            lock (_sync)
            {
                FinalText = string.Empty;
            }
        }

        public void OpenExchange(BridgeExchange exchange)
        {
            lock (_sync)
            {
                CurrentExchange = exchange;
            }
        }

        public void CloseExchange(BridgeExchange exchange)
        {
            lock (_sync)
            {
                if (ReferenceEquals(CurrentExchange, exchange))
                {
                    CurrentExchange = null;
                }
            }
        }

        public void MarkWaitingForToolOutput(PendingToolCall pendingToolCall)
        {
            lock (_sync)
            {
                _pendingToolCallIds.Add(pendingToolCall.CallId);
            }
        }

        public void MarkToolOutputSubmitted(PendingToolCall pendingToolCall)
        {
            lock (_sync)
            {
                _pendingToolCallIds.Remove(pendingToolCall.CallId);
            }
        }

        public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    }
}
