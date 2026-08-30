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

internal enum PendingToolOutputMatchKind
{
    None,
    Pending,
    Retired
}

internal sealed class PendingToolCallStore(
    ILogger<PendingToolCallStore> logger,
    ConversationSessionStore? conversationSessions = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingCalls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _retiredCallIds = new(StringComparer.Ordinal);
    private int _disposed;

    public bool TryAdd(PendingToolCall pendingCall)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            pendingCall.Touch(DateTimeOffset.UtcNow);
            if (!_pendingCalls.TryAdd(pendingCall.CallId, pendingCall))
            {
                return false;
            }

            return true;
        }
    }

    public bool ContainsCallId(string callId) => _pendingCalls.ContainsKey(callId);

    public bool ContainsTrailingRetiredOutput(ResponsesApiRequest request)
    {
        lock (_sync)
        {
            return ContainsTrailingRetiredOutputCore(request);
        }
    }

    public PendingToolOutputMatchKind FindOutputs(
        ResponsesApiRequest request,
        out IReadOnlyList<PendingToolOutput> pendingToolOutputs,
        int maxToolOutputChars = 0)
    {
        lock (_sync)
        {
            if (TryFindOutputsCore(request, out pendingToolOutputs, maxToolOutputChars))
            {
                return PendingToolOutputMatchKind.Pending;
            }

            return ContainsTrailingRetiredOutputCore(request)
                ? PendingToolOutputMatchKind.Retired
                : PendingToolOutputMatchKind.None;
        }
    }

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

            if (TryRemove(match.Pending))
            {
                match.Pending.Touch(DateTimeOffset.UtcNow);
                outputs.Add(new PendingToolOutput(match.Pending, match.Output!));
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
        lock (_sync)
        {
            return TryFindOutputsCore(request, out pendingToolOutputs, maxToolOutputChars);
        }
    }

    private bool TryFindOutputsCore(
        ResponsesApiRequest request,
        out IReadOnlyList<PendingToolOutput> pendingToolOutputs,
        int maxToolOutputChars)
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

    public bool TryRemove(PendingToolCall pendingCall)
    {
        lock (_sync)
        {
            var removed = ((ICollection<KeyValuePair<string, PendingToolCall>>)_pendingCalls)
                .Remove(new KeyValuePair<string, PendingToolCall>(pendingCall.CallId, pendingCall));
            if (removed)
            {
                RetireCallId(pendingCall.CallId);
            }

            return removed;
        }
    }

    public int RemoveForSession(IPendingBridgeSession session)
    {
        lock (_sync)
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
                    RetireCallId(pending.Key);
                    removedCount++;
                }
            }

            return removedCount;
        }
    }

    private Match[] FindMatches(ResponsesApiRequest request, int maxToolOutputChars)
    {
        var now = DateTimeOffset.UtcNow;
        return _pendingCalls.Values
            .Select(pending => new Match(
                pending,
                ToolOutputReader.FindTrailingTextOutput(request, pending.CallId, maxToolOutputChars)))
            .Where(match =>
            {
                if (match.Output is null)
                {
                    return false;
                }

                match.Pending.Touch(now);
                return true;
            })
            .ToArray();
    }

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

        lock (_sync)
        {
            foreach (var retiredCallId in _retiredCallIds)
            {
                if (now.UtcTicks - retiredCallId.Value >= ttl.Ticks)
                {
                    ((ICollection<KeyValuePair<string, long>>)_retiredCallIds).Remove(retiredCallId);
                }
            }
        }

        var expiredSessions = _pendingCalls
            .Where(pending => now - pending.Value.LastSeenAt >= ttl)
            .Select(pending => pending.Value.Session)
            .Distinct<IPendingBridgeSession>(ReferenceEqualityComparer.Instance)
            .ToArray();
        var removedCount = 0;

        foreach (var session in expiredSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PendingToolCall[] removedCalls;
            lock (_sync)
            {
                var stillExpired = _pendingCalls.Values.Any(pending =>
                    ReferenceEquals(pending.Session, session) &&
                    now - pending.LastSeenAt >= ttl);
                if (!stillExpired ||
                    session is IReusableBridgeSession reusableSession &&
                    !reusableSession.TryRetireForPendingToolExpiry())
                {
                    continue;
                }

                removedCalls = _pendingCalls
                    .Where(pending => ReferenceEquals(pending.Value.Session, session))
                    .Select(pending => pending.Value)
                    .ToArray();
                foreach (var removed in removedCalls)
                {
                    if (((ICollection<KeyValuePair<string, PendingToolCall>>)_pendingCalls)
                        .Remove(new KeyValuePair<string, PendingToolCall>(removed.CallId, removed)))
                    {
                        RetireCallId(removed.CallId);
                        removedCount++;
                    }
                }
            }

            foreach (var removed in removedCalls)
            {
                logger.LogWarning(
                    "Expired pending Copilot tool call. CallId={CallId} Tool={ToolName} Model={Model}",
                    removed.CallId,
                    removed.Tool.CopilotName,
                    removed.Session.Model);
            }

            conversationSessions?.RemoveForSession(session);
            await session.DisposeAsync();
        }

        return removedCount;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PendingToolCall[] pendingCalls;
        lock (_sync)
        {
            pendingCalls = _pendingCalls.Values.ToArray();
            _pendingCalls.Clear();
            _retiredCallIds.Clear();
        }

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

    private void RetireCallId(string callId) =>
        _retiredCallIds[callId] = DateTimeOffset.UtcNow.UtcTicks;

    private bool ContainsTrailingRetiredOutputCore(ResponsesApiRequest request) =>
        ToolOutputReader.FindTrailingOutputCallIds(request)
            .Any(callId => _retiredCallIds.ContainsKey(callId));
}
