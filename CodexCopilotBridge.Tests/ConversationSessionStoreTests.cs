using CodexCopilotBridge.Inference;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexCopilotBridge.Tests;

public sealed class ConversationSessionStoreTests
{
    [Fact]
    public async Task ExpireAsync_RemovesIdleSessions()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.AddOrReplaceAsync(session);
        session.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));

        var expired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(1, expired);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(0, store.Snapshot().ActiveSessionCount);
    }

    [Fact]
    public async Task ExpireAsync_KeepsSessionsWaitingForToolOutput()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow.AddMinutes(-10))
        {
            WaitingForToolOutput = true
        };
        await store.AddOrReplaceAsync(session);
        session.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));

        var expired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(0, expired);
        Assert.Equal(0, session.DisposeCount);
        Assert.Equal(1, store.Snapshot().ActiveSessionCount);
    }

    [Fact]
    public async Task ExpireAsync_KeepsActiveSessions()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow.AddMinutes(-10))
        {
            ExplicitlyActive = true
        };
        await store.AddOrReplaceAsync(session);
        session.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));

        var expired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(0, expired);
        Assert.Equal(0, session.DisposeCount);
        Assert.Equal(1, store.Snapshot().ActiveSessionCount);
    }

    [Fact]
    public async Task Snapshot_DoesNotCountActiveSessionsAsIdle()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow.AddMinutes(-10))
        {
            ExplicitlyActive = true
        };
        await store.AddOrReplaceAsync(session);

        var snapshot = store.Snapshot(DateTimeOffset.UtcNow);

        Assert.Equal(1, snapshot.ActiveSessionCount);
        Assert.Null(snapshot.OldestIdleSessionAgeSeconds);
    }

    [Fact]
    public async Task ExpireAsync_KeepsReservedSessionsUntilReleased()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.AddOrReplaceAsync(session);
        session.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.True(store.TryGet("thread:one", out var reserved));
        reserved.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));

        var reservedExpired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        reserved.ReleaseReservation();
        reserved.Touch(DateTimeOffset.UtcNow.AddMinutes(-10));
        var releasedExpired = await store.ExpireAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(0, reservedExpired);
        Assert.Equal(1, releasedExpired);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task AddOrReplaceAsync_DisposesReplacedSession()
    {
        var store = CreateStore();
        var oldSession = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow);
        var newSession = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow);
        await store.AddOrReplaceAsync(oldSession);

        await store.AddOrReplaceAsync(newSession);

        Assert.Equal(1, oldSession.DisposeCount);
        Assert.True(store.TryGet("thread:one", out var stored));
        Assert.Same(newSession, stored);
    }

    [Fact]
    public async Task TryGet_RemovesSessionThatHasStartedRetiring()
    {
        var store = CreateStore();
        var session = new FakeReusableSession("thread:one", DateTimeOffset.UtcNow);
        await store.AddOrReplaceAsync(session);
        Assert.True(session.TryRetireIfIdle());

        var found = store.TryGet("thread:one", out _);

        Assert.False(found);
        Assert.Equal(0, store.Snapshot().ActiveSessionCount);
    }

    private static ConversationSessionStore CreateStore() =>
        new(NullLogger<ConversationSessionStore>.Instance);

    private sealed class FakeReusableSession(string conversationKey, DateTimeOffset lastSeenAt) : IReusableBridgeSession
    {
        private long _lastSeenTicks = lastSeenAt.UtcTicks;
        private int _reservationCount;
        private bool _retired;

        public string ConversationKey { get; } = conversationKey;

        public string Model { get; } = "test-model";

        public DateTimeOffset CreatedAt { get; } = lastSeenAt;

        public DateTimeOffset LastSeenAt => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

        public bool WaitingForToolOutput { get; init; }

        public bool ExplicitlyActive { get; init; }

        public bool IsActive => ExplicitlyActive || _reservationCount > 0;

        public int DisposeCount { get; private set; }

        public bool TryReserve()
        {
            if (_retired)
            {
                return false;
            }

            _reservationCount++;
            return true;
        }

        public void ReleaseReservation()
        {
            if (_reservationCount > 0)
            {
                _reservationCount--;
            }
        }

        public bool TryRetireIfIdle()
        {
            if (_retired || WaitingForToolOutput || IsActive)
            {
                return false;
            }

            _retired = true;
            return true;
        }

        public bool TryRetireForPendingToolExpiry() =>
            TryRetireIfIdle();

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
