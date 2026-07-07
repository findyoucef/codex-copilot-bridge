using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodexCopilotBridge.Inference;

internal static class BridgeResponseRunner
{
    public static async IAsyncEnumerable<BridgeOutput> RunAsync(
        BridgeOptions options,
        Func<ChannelWriter<BridgeOutput>, CancellationToken, Task> operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = BridgeOutputChannels.CreateBounded(options.ResponseOutputChannelCapacity);

        _ = Task.Run(async () =>
        {
            var requestTimeout = Positive(options.RequestTimeout, TimeSpan.FromHours(1));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);

            try
            {
                await operation(channel.Writer, timeout.Token);
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

    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}
