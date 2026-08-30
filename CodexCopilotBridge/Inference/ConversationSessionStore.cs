using System.Collections.Concurrent;

namespace CodexCopilotBridge.Inference;

internal interface IReusableBridgeSession : IPendingBridgeSession
{
    string ConversationKey { get; }

    DateTimeOffset CreatedAt { get; }

    DateTimeOffset LastSeenAt { get; }

    bool WaitingForToolOutput { get; }

    bool IsActive { get; }

    bool TryReserve();

    void ReleaseReservation();

    bool TryRetireIfIdle();

    bool TryRetireForPendingToolExpiry();

    void Touch(DateTimeOffset now);
}

internal sealed record ConversationSessionStoreSnapshot(
    int ActiveSessionCount,
    double? OldestSessionAgeSeconds,
    double? OldestIdleSessionAgeSeconds);

internal sealed class BridgeSessionLifetime(Func<ValueTask> disposeSession)
{
    private readonly object _sync = new();
    private int _reservationCount;
    private bool _retired;
    private TaskCompletionSource? _retiredAndIdle;
    private Task? _disposeTask;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _reservationCount > 0;
            }
        }
    }

    public bool IsRetired
    {
        get
        {
            lock (_sync)
            {
                return _retired;
            }
        }
    }

    public bool TryReserve()
    {
        lock (_sync)
        {
            if (_retired)
            {
                return false;
            }

            _reservationCount++;
            return true;
        }
    }

    public void ReleaseReservation()
    {
        TaskCompletionSource? retiredAndIdle = null;
        lock (_sync)
        {
            if (_reservationCount > 0)
            {
                _reservationCount--;
            }

            if (_retired && _reservationCount == 0)
            {
                retiredAndIdle = _retiredAndIdle;
            }
        }

        retiredAndIdle?.TrySetResult();
    }

    public bool TryRetireIfIdle()
    {
        lock (_sync)
        {
            if (_retired || _reservationCount > 0)
            {
                return false;
            }

            _retired = true;
            return true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _retired = true;
            _retiredAndIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_reservationCount == 0)
            {
                _retiredAndIdle.TrySetResult();
            }

            _disposeTask = DisposeWhenIdleAsync(_retiredAndIdle.Task);
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeWhenIdleAsync(Task retiredAndIdle)
    {
        await Task.Yield();
        await retiredAndIdle;
        await disposeSession();
    }
}

internal sealed class ConversationSessionStore(ILogger<ConversationSessionStore> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IReusableBridgeSession> _sessions = new(StringComparer.Ordinal);

    public bool TryGet(string conversationKey, out IReusableBridgeSession session)
    {
        if (_sessions.TryGetValue(conversationKey, out session!))
        {
            if (session.TryReserve())
            {
                session.Touch(DateTimeOffset.UtcNow);
                return true;
            }

            TryRemove(conversationKey, session);
        }

        session = null!;
        return false;
    }

    public async ValueTask AddOrReplaceAsync(IReusableBridgeSession session)
    {
        session.Touch(DateTimeOffset.UtcNow);

        while (true)
        {
            if (!_sessions.TryGetValue(session.ConversationKey, out var replaced))
            {
                if (_sessions.TryAdd(session.ConversationKey, session))
                {
                    return;
                }

                continue;
            }

            if (ReferenceEquals(replaced, session))
            {
                return;
            }

            if (TryRemove(session.ConversationKey, replaced))
            {
                await replaced.DisposeAsync();
                continue;
            }
        }
    }

    public bool Remove(string conversationKey, out IReusableBridgeSession session) =>
        _sessions.TryRemove(conversationKey, out session!);

    public int RemoveForSession(IPendingBridgeSession session)
    {
        var removedCount = 0;
        foreach (var current in _sessions)
        {
            if (!ReferenceEquals(current.Value, session))
            {
                continue;
            }

            if (TryRemove(current.Key, current.Value))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    public ConversationSessionStoreSnapshot Snapshot(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var sessions = _sessions.Values.ToArray();
        var oldestSessionAgeSeconds = sessions.Length == 0
            ? (double?)null
            : sessions.Max(session => Math.Max(0, (timestamp - session.CreatedAt).TotalSeconds));
        var idleSessions = sessions
            .Where(session => !session.WaitingForToolOutput && !session.IsActive)
            .ToArray();
        var oldestIdleSessionAgeSeconds = idleSessions.Length == 0
            ? (double?)null
            : idleSessions.Max(session => Math.Max(0, (timestamp - session.LastSeenAt).TotalSeconds));

        return new ConversationSessionStoreSnapshot(
            sessions.Length,
            oldestSessionAgeSeconds,
            oldestIdleSessionAgeSeconds);
    }

    public async ValueTask<int> ExpireAsync(DateTimeOffset now, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromHours(2);
        }

        var expired = _sessions
            .Where(session => !session.Value.WaitingForToolOutput &&
                !session.Value.IsActive &&
                now - session.Value.LastSeenAt >= ttl)
            .ToArray();
        var removedCount = 0;

        foreach (var candidate in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateSession = candidate.Value;
            if (candidateSession.WaitingForToolOutput ||
                candidateSession.IsActive ||
                now - candidateSession.LastSeenAt < ttl ||
                !candidateSession.TryRetireIfIdle())
            {
                continue;
            }

            if (!TryRemove(candidate.Key, candidateSession))
            {
                await candidateSession.DisposeAsync();
                continue;
            }

            removedCount++;
            await candidateSession.DisposeAsync();
            logger.LogInformation(
                "Expired reusable Copilot conversation session. ConversationKey={ConversationKey} Model={Model}",
                candidateSession.ConversationKey,
                candidateSession.Model);
        }

        return removedCount;
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();

        var disposedSessions = new HashSet<IReusableBridgeSession>(ReferenceEqualityComparer.Instance);
        foreach (var session in sessions)
        {
            if (disposedSessions.Add(session))
            {
                await session.DisposeAsync();
            }
        }
    }

    private bool TryRemove(string conversationKey, IReusableBridgeSession expectedSession) =>
        ((ICollection<KeyValuePair<string, IReusableBridgeSession>>)_sessions)
        .Remove(new KeyValuePair<string, IReusableBridgeSession>(conversationKey, expectedSession));
}
