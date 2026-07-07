using CodexCopilotBridge;
using CodexCopilotBridge.Inference;

namespace CodexCopilotBridge.Tests;

public sealed class BridgeResponseRunnerTests
{
    [Fact]
    public async Task RunAsync_StreamsOutputsFromOperation()
    {
        var outputs = new List<BridgeOutput>();
        var options = new BridgeOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(1),
            ResponseOutputChannelCapacity = 1
        };

        await foreach (var output in BridgeResponseRunner.RunAsync(
                           options,
                           async (writer, cancellationToken) =>
                           {
                               await writer.WriteAsync(new TextDeltaOutput("ok"), cancellationToken);
                           },
                           CancellationToken.None))
        {
            outputs.Add(output);
        }

        var delta = Assert.IsType<TextDeltaOutput>(Assert.Single(outputs));
        Assert.Equal("ok", delta.Delta);
    }

    [Fact]
    public async Task RunAsync_TimesOutLongRunningOperation()
    {
        var options = new BridgeOptions
        {
            RequestTimeout = TimeSpan.FromMilliseconds(20),
            ResponseOutputChannelCapacity = 1
        };

        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in BridgeResponseRunner.RunAsync(
                               options,
                               async (_, cancellationToken) => await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken),
                               CancellationToken.None))
            {
            }
        });

        Assert.Contains("timed out", exception.Message);
    }
}
