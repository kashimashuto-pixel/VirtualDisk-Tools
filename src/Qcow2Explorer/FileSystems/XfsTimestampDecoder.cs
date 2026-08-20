using System.Buffers.Binary;

namespace Qcow2Explorer.FileSystems;

internal static class XfsTimestampDecoder
{
    private const long NanosecondsPerSecond = 1_000_000_000;
    private const long BigTimeEpochOffsetSeconds = 2_147_483_648;

    public static DateTime? Decode(ReadOnlySpan<byte> data, bool bigTime)
    {
        if (data.Length < sizeof(ulong))
        {
            return null;
        }

        long seconds;
        uint nanoseconds;
        if (bigTime)
        {
            var encoded = BinaryPrimitives.ReadUInt64BigEndian(data);
            var bigTimeSeconds = encoded / NanosecondsPerSecond;
            seconds = checked((long)bigTimeSeconds - BigTimeEpochOffsetSeconds);
            nanoseconds = (uint)(encoded % NanosecondsPerSecond);
        }
        else
        {
            seconds = BinaryPrimitives.ReadInt32BigEndian(data);
            nanoseconds = BinaryPrimitives.ReadUInt32BigEndian(data[sizeof(uint)..]);
        }

        if (nanoseconds >= NanosecondsPerSecond)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds)
                .AddTicks(nanoseconds / 100)
                .UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
