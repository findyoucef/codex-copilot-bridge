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

internal sealed class PendingToolCallStore(
    ILogger<PendingToolCallStore> logger,
    ConversationSessionStore? conversationSessions = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);

    public bool TryAdd(PendingToolCall pendingCall)
    {
        pendingCall.Touch(DateTimeOffset.UtcNow);
        return _pendingCalls.TryAdd(pendingCall.CallId, pendingCall);
    }

    public bool ContainsCallId(string callId) => _pendingCalls.ContainsKey(callId);

    public bool TryTakeOutputs(
        ResponsesApiRequest request,
        out IReadOnlyList<PendingToolOutput> pendingToolOutputs,
        int maxToolOutputChars = 0)
    {
        var matches = FindMatches(request, maxToolOutputChars);

        if (matches.Length == 0)
        {
            pendingToolOutputs = [];
            return false;
        }

        var sessionCounts = new Dictionary<IPendingBridgeSession, int>(ReferenceEqualityComparer.Instance);
        foreach (var match in matches)
        {
            sessionCounts.TryGetValue(match.Pending.Session, out var count);
            sessionCounts[match.Pending.Session] = count + 1;
        }

        var session = sessionCounts.MaxBy(match => match.Value).Key;
        var outputs = new List<PendingToolOutput>();

        foreach (var match in matches)
        {
            if (!ReferenceEquals(match.Pending.Session, session))
            {
                continue;
            }

            if (_pendingCalls.TryRemove(match.Pending.CallId, out var removed))
            {
                removed.Touch(DateTimeOffset.UtcNow);
                outputs.Add(new PendingToolOutput(removed, match.Output!));
            }
        }

        pendingToolOutputs = outputs;
        return outputs.Count > 0;
    }

    public bool TryFindOutputs(
        ResponsesApiRequest request,
        out IReadOnlyList<PendingToolOutput> pendingToolOutputs,
        int maxToolOutputChars = 0)
    {
        var matches = FindMatches(request, maxToolOutputChars);

        if (matches.Length == 0)
        {
            pendingToolOutputs = [];
            return false;
        }

        pendingToolOutputs = SelectBestSessionOutputs(matches);
        return pendingToolOutputs.Count > 0;
    }

    public bool TryRemove(PendingToolCall pendingCall) =>
        ((ICollection<KeyValuePair<string, PendingToolCall>>)_pendingCalls)
        .Remove(new KeyValuePair<string, PendingToolCall>(pendingCall.CallId, pendingCall));

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

    private Match[] FindMatches(ResponsesApiRequest request, int maxToolOutputChars) =>
        _pendingCalls.Values
            .Select(pending => new Match(
                pending,
                ToolOutputReader.FindTextOutput(request, pending.CallId, maxToolOutputChars)))
            .Where(match => match.Output is not null)
            .ToArray();

    private static IReadOnlyList<PendingToolOutput> SelectBestSessionOutputs(IReadOnlyList<Match> matches)
    {
        var sessionCounts = new Dictionary<IPendingBridgeSession, int>(ReferenceEqualityComparer.Instance);
        foreach (var match in matches)
        {
            sessionCounts.TryGetValue(match.Pending.Session, out var count);
            sessionCounts[match.Pending.Session] = count + 1;
        }

        var session = sessionCounts.MaxBy(match => match.Value).Key;
        return matches
            .Where(match => ReferenceEquals(match.Pending.Session, session))
            .Select(match => new PendingToolOutput(match.Pending, match.Output!))
            .ToArray();
    }

    private sealed record Match(PendingToolCall Pending, string? Output);

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
            ttl = TimeSpan.FromHours(1);
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
                conversationSessions?.RemoveForSession(removed.Session);
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
                conversationSessions?.RemoveForSession(pending.Session);
                await pending.Session.DisposeAsync();
            }
        }
    }
}
