using System.Threading.Channels;

namespace CodexCopilotBridge.Inference;

internal static class BridgeOutputChannels
{
    public const int DefaultCapacity = 512;

    public static Channel<BridgeOutput> CreateBounded(int capacity) =>
        Channel.CreateBounded<BridgeOutput>(
            new BoundedChannelOptions(PositiveCapacity(capacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    public static int PositiveCapacity(int capacity) =>
        capacity > 0 ? capacity : DefaultCapacity;
}
