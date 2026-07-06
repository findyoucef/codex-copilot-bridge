using System.Text;
using System.Threading.Channels;
using CodexCopilotBridge.Protocol;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace CodexCopilotBridge.Inference;

internal sealed class CopilotToolBridgeInferenceBridge(
    IOptions<BridgeOptions> options,
    PendingToolCallStore pendingToolCalls,
    ILogger<CopilotToolBridgeInferenceBridge> logger) : IInferenceBridge, IAsyncDisposable
{
    private static readonly TimeSpan DefaultToolRequestBatchQuietPeriod = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private CopilotClient? _client;

    public async IAsyncEnumerable<BridgeOutput> CreateResponseAsync(
        ResponsesApiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = BridgeOutputChannels.CreateBounded(options.Value.ResponseOutputChannelCapacity);

        _ = Task.Run(async () =>
        {
            var requestTimeout = Positive(options.Value.RequestTimeout, TimeSpan.FromHours(1));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);

            try
            {
                if (pendingToolCalls.TryTakeOutputs(request, out var pendingToolOutputs))
                {
                    await ContinuePendingToolCallsAsync(pendingToolOutputs, channel.Writer, timeout.Token);
                }
                else
                {
                    await StartCopilotTurnAsync(request, channel.Writer, timeout.Token);
                }

                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete(new TimeoutException($"Copilot bridge request timed out after {requestTimeout}."));
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

    public async ValueTask DisposeAsync()
    {
        await pendingToolCalls.DisposeAsync();

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
        }
    }

    private async Task StartCopilotTurnAsync(
        ResponsesApiRequest request,
        ChannelWriter<BridgeOutput> writer,
        CancellationToken cancellationToken)
    {
        var tools = CodexToolReader.ReadToolDeclarations(request);
        var state = await CreateCopilotStateAsync(request, tools, cancellationToken);
        var exchange = new BridgeExchange(writer, cancellationToken);
        var disposeState = false;
        var gateHeld = false;

        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            gateHeld = true;
            state.OpenExchange(exchange);

            await state.Session.SendAsync(
                new MessageOptions
                {
                    Prompt = CodexPromptRenderer.Render(request, includeToolNotice: false)
                },
                cancellationToken);

            await exchange.Done.Task.WaitAsync(cancellationToken);

            if (!state.WaitingForToolOutput)
            {
                await writer.WriteAsync(new FinalTextOutput(state.FinalText), cancellationToken);
                disposeState = true;
            }
        }
        catch
        {
            disposeState = true;
            throw;
        }
        finally
        {
            if (gateHeld)
            {
                state.CloseExchange(exchange);
                state.Gate.Release();
            }

            exchange.Dispose();

            if (disposeState)
            {
                pendingToolCalls.RemoveForSession(state);
                await state.DisposeAsync();
            }
        }
    }

    private async Task ContinuePendingToolCallsAsync(
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

        var exchange = new BridgeExchange(writer, cancellationToken);
        var disposeState = false;
        var gateHeld = false;

        try
        {
            await state.Gate.WaitAsync(cancellationToken);
            gateHeld = true;

            state.OpenExchange(exchange);

            foreach (var pendingToolOutput in pendingToolOutputs)
            {
                pending = pendingToolOutput.PendingCall;
                logger.LogInformation(
                    "Returning Codex tool output to Copilot. CallId={CallId} Tool={ToolName}",
                    pending.CallId,
                    pending.Tool.CopilotName);

                await state.Session.Rpc.Tools.HandlePendingToolCallAsync(
                    pending.CopilotRequestId,
                    pendingToolOutput.Output,
                    error: null!,
                    cancellationToken);

                state.MarkToolOutputSubmitted(pending);
            }

            await exchange.Done.Task.WaitAsync(cancellationToken);

            if (!state.WaitingForToolOutput)
            {
                await writer.WriteAsync(new FinalTextOutput(state.FinalText), cancellationToken);
                disposeState = true;
            }
        }
        catch
        {
            disposeState = true;
            throw;
        }
        finally
        {
            if (gateHeld)
            {
                state.CloseExchange(exchange);
                state.Gate.Release();
            }

            exchange.Dispose();

            if (disposeState)
            {
                pendingToolCalls.RemoveForSession(state);
                await state.DisposeAsync();
            }
        }
    }

    private async Task<CopilotBridgeState> CreateCopilotStateAsync(
        ResponsesApiRequest request,
        IReadOnlyList<CodexToolDeclaration> tools,
        CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Value.DefaultModel : request.Model;
        var client = await GetCopilotClientAsync(cancellationToken);

        var config = new SessionConfig
        {
            Model = model,
            Streaming = true,
            SkipCustomInstructions = true,
            EnableSkills = false,
            OnPermissionRequest = PermissionHandler.ApproveAll
        };

        if (tools.Count > 0)
        {
            config.Tools = tools.Select(CreateCopilotToolDeclaration).ToList();
            config.AvailableTools = tools.Select(tool => tool.CopilotName).ToList();
        }

        var session = await client.CreateSessionAsync(config, cancellationToken);
        var state = new CopilotBridgeState(session, tools, model);
        session.On<SessionEvent>(evt => HandleSessionEvent(state, evt));

        logger.LogInformation("Started Copilot bridge session with {ToolCount} Codex tool declaration(s).", tools.Count);
        return state;
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

        if (!state.ToolsByCopilotName.TryGetValue(request.ToolName, out var tool))
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
            RenderArguments(request),
            tool.CodexNamespace));
        exchange.CompleteAfterQuietPeriod(Positive(options.Value.ToolRequestBatchQuietPeriod, DefaultToolRequestBatchQuietPeriod));
    }

    private static AIFunctionDeclaration CreateCopilotToolDeclaration(CodexToolDeclaration tool) =>
        AIFunctionFactory.CreateDeclaration(
            tool.CopilotName,
            string.IsNullOrWhiteSpace(tool.Description)
                ? $"Execute the Codex {tool.CodexName} tool."
                : tool.Description,
            tool.Parameters,
            returnJsonSchema: null);

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

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(BridgeOutput output)
        {
            try
            {
                writer.WriteAsync(output, cancellationToken).AsTask().GetAwaiter().GetResult();
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
        string model) : IPendingBridgeSession
    {
        private readonly object _sync = new();
        private bool _disposed;
        private readonly HashSet<string> _pendingToolCallIds = new(StringComparer.Ordinal);

        public CopilotSession Session { get; } = session;

        public string Model { get; } = model;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyDictionary<string, CodexToolDeclaration> ToolsByCopilotName { get; } =
            tools.ToDictionary(tool => tool.CopilotName, StringComparer.Ordinal);

        public BridgeExchange? CurrentExchange { get; private set; }

        public string FinalText { get; set; } = string.Empty;

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

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await Session.DisposeAsync();
            Gate.Dispose();
        }
    }
}
