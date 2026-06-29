using System.Collections.Concurrent;
using CodexCopilotBridge.Protocol;

namespace CodexCopilotBridge.Inference;

internal interface IPendingBridgeSession : IAsyncDisposable
{
    string Model { get; }
}

internal sealed class PendingToolCall(
    string callId,
    string copilotRequestId,
    IPendingBridgeSession session,
    CodexToolDeclaration tool,
    DateTimeOffset createdAt)
{
    private long _lastSeenTicks = createdAt.UtcTicks;

    public string CallId { get; } = callId;

    public string CopilotRequestId { get; } = copilotRequestId;

    public IPendingBridgeSession Session { get; } = session;

    public CodexToolDeclaration Tool { get; } = tool;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);
}

internal sealed record PendingToolOutput(PendingToolCall PendingCall, string Output);

internal sealed record PendingToolCallStoreSnapshot(
    int PendingToolCallCount,
    int ActiveSessionCount,
    double? OldestPendingCallAgeSeconds);

internal sealed class PendingToolCallStore(ILogger<PendingToolCallStore> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);

    public bool TryAdd(PendingToolCall pendingCall)
    {
        pendingCall.Touch(DateTimeOffset.UtcNow);
        return _pendingCalls.TryAdd(pendingCall.CallId, pendingCall);
    }

    public bool ContainsCallId(string callId) => _pendingCalls.ContainsKey(callId);

    public bool TryTakeOutput(ResponsesApiRequest request, out PendingToolOutput pendingToolOutput)
    {
        foreach (var pending in _pendingCalls.Values)
        {
            var output = ToolOutputReader.FindTextOutput(request, pending.CallId);
            if (output is null)
            {
                continue;
            }

            if (_pendingCalls.TryRemove(pending.CallId, out var removed))
            {
                removed.Touch(DateTimeOffset.UtcNow);
                pendingToolOutput = new PendingToolOutput(removed, output);
                return true;
            }
        }

        pendingToolOutput = null!;
        return false;
    }

    public int RemoveForSession(IPendingBridgeSession session)
    {
        var removedCount = 0;
        foreach (var pending in _pendingCalls)
        {
            if (!ReferenceEquals(pending.Value.Session, session))
            {
                continue;
            }

            if (_pendingCalls.TryRemove(pending.Key, out _))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    public PendingToolCallStoreSnapshot Snapshot(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var pendingCalls = _pendingCalls.Values.ToArray();
        var activeSessionCount = pendingCalls
            .Select(pending => pending.Session)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        var oldestAgeSeconds = pendingCalls.Length == 0
            ? (double?)null
            : pendingCalls.Max(pending => Math.Max(0, (timestamp - pending.CreatedAt).TotalSeconds));

        return new PendingToolCallStoreSnapshot(pendingCalls.Length, activeSessionCount, oldestAgeSeconds);
    }

    public async ValueTask<int> ExpireAsync(DateTimeOffset now, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromMinutes(10);
        }

        var expired = _pendingCalls
            .Where(pending => now - pending.Value.LastSeenAt >= ttl)
            .ToArray();
        var disposedSessions = new HashSet<IPendingBridgeSession>(ReferenceEqualityComparer.Instance);
        var removedCount = 0;

        foreach (var candidate in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_pendingCalls.TryRemove(candidate.Key, out var removed))
            {
                continue;
            }

            removedCount++;
            if (disposedSessions.Add(removed.Session))
            {
                await removed.Session.DisposeAsync();
            }

            logger.LogWarning(
                "Expired pending Copilot tool call. CallId={CallId} Tool={ToolName} Model={Model}",
                removed.CallId,
                removed.Tool.CopilotName,
                removed.Session.Model);
        }

        return removedCount;
    }

    public async ValueTask DisposeAsync()
    {
        var pendingCalls = _pendingCalls.Values.ToArray();
        _pendingCalls.Clear();

        var disposedSessions = new HashSet<IPendingBridgeSession>(ReferenceEqualityComparer.Instance);
        foreach (var pending in pendingCalls)
        {
            if (disposedSessions.Add(pending.Session))
            {
                await pending.Session.DisposeAsync();
            }
        }
    }
}
