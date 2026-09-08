using System.Buffers.Binary;
using EmbyPlayer.Core.Playback;

namespace EmbyPlayer.Emby;

// Roku BIF v0: https://developer.roku.com/dev/docs/bif-file-creation
internal sealed class BifPreviewArchive(byte[] data, long[] ticks, int[] offsets)
{
    public bool IsEmpty => ticks.Length == 0;

    public static BifPreviewArchive? Parse(byte[] data)
    {
        if (data.Length < 72 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 66, 73, 70, 13, 10, 26, 10 })
            || Read(data, 8) != 0) return null;
        var count = Read(data, 12);
        if (count > 100_000 || 64L + (count + 1L) * 8 > data.Length) return null;
        var multiplier = Read(data, 16);
        if (multiplier == 0) multiplier = 1000;
        var ticks = new long[count];
        var offsets = new int[count + 1];
        for (var index = 0; index <= count; index++)
        {
            var stamp = Read(data, 64 + index * 8);
            var offset = Read(data, 68 + index * 8);
            if (offset < 64L + (count + 1L) * 8 || offset > data.Length
                || (index > 0 && offset <= offsets[index - 1])) return null;
            offsets[index] = (int)offset;
            if (index == count)
            {
                if (stamp != uint.MaxValue || offset != data.Length) return null;
                continue;
            }
            var milliseconds = (ulong)stamp * multiplier;
            if (milliseconds > (ulong)(long.MaxValue / TimeSpan.TicksPerMillisecond)) return null;
            ticks[index] = (long)milliseconds * TimeSpan.TicksPerMillisecond;
            if (index > 0 && ticks[index] <= ticks[index - 1]) return null;
        }
        return new(data, ticks, offsets);
    }

    public PlaybackPreviewResult GetFrame(TimeSpan position)
    {
        if (IsEmpty) return PlaybackPreviewResult.Failure(PlaybackPreviewError.Unavailable);
        var index = Array.BinarySearch(ticks, Math.Max(0, position.Ticks));
        if (index < 0) index = Math.Max(0, ~index - 1);
        var length = offsets[index + 1] - offsets[index];
        // Bound encoded frame size, and let the UI decoder validate the JPEG itself.
        if (length < 4 || length > 2 * 1024 * 1024 || data[offsets[index]] != 255 || data[offsets[index] + 1] != 216)
            return PlaybackPreviewResult.Failure(PlaybackPreviewError.InvalidResponse);
        return new(data.AsSpan(offsets[index], length).ToArray(), TimeSpan.FromTicks(ticks[index]), PlaybackPreviewError.None);
    }

    private static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
