using CodexCopilotBridge.Inference;

namespace CodexCopilotBridge.Tests;

public sealed class BridgeSessionLifetimeTests
{
    [Fact]
    public async Task DisposeAsync_WaitsForLateContinuationReservation()
    {
        var disposeCount = 0;
        var lifetime = new BridgeSessionLifetime(() =>
        {
            disposeCount++;
            return ValueTask.CompletedTask;
        });
        Assert.True(lifetime.TryReserve());

        var disposal = lifetime.DisposeAsync();

        Assert.False(disposal.IsCompleted);
        Assert.False(lifetime.TryReserve());
        Assert.Equal(0, disposeCount);

        lifetime.ReleaseReservation();
        await disposal;

        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DisposesOnlyOnce()
    {
        var disposeCount = 0;
        var lifetime = new BridgeSessionLifetime(() =>
        {
            disposeCount++;
            return ValueTask.CompletedTask;
        });

        await lifetime.DisposeAsync();
        await lifetime.DisposeAsync();

        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public void TryRetireIfIdle_IsAtomicWithReservations()
    {
        var lifetime = new BridgeSessionLifetime(() => ValueTask.CompletedTask);
        Assert.True(lifetime.TryReserve());

        Assert.False(lifetime.TryRetireIfIdle());
        lifetime.ReleaseReservation();
        Assert.True(lifetime.TryRetireIfIdle());
        Assert.False(lifetime.TryReserve());
    }
}
