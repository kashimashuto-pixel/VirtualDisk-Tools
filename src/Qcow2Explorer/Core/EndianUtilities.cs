using System.Text;

namespace Qcow2Explorer.Core;

public static class EndianUtilities
{
    public static ushort ReadUInt16Little(byte[] buffer, int offset)
    {
        return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    public static short ReadInt16Little(byte[] buffer, int offset)
    {
        return unchecked((short)ReadUInt16Little(buffer, offset));
    }

    public static uint ReadUInt32Little(byte[] buffer, int offset)
    {
        return (uint)(buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24));
    }

    public static int ReadInt32Little(byte[] buffer, int offset)
    {
        return unchecked((int)ReadUInt32Little(buffer, offset));
    }

    public static ulong ReadUInt64Little(byte[] buffer, int offset)
    {
        var lo = ReadUInt32Little(buffer, offset);
        var hi = ReadUInt32Little(buffer, offset + 4);
        return lo | ((ulong)hi << 32);
    }

    public static long ReadInt64Little(byte[] buffer, int offset)
    {
        return unchecked((long)ReadUInt64Little(buffer, offset));
    }

    public static uint ReadUInt32Big(byte[] buffer, int offset)
    {
        return ((uint)buffer[offset] << 24)
            | ((uint)buffer[offset + 1] << 16)
            | ((uint)buffer[offset + 2] << 8)
            | buffer[offset + 3];
    }

    public static ulong ReadUInt64Big(byte[] buffer, int offset)
    {
        var hi = ReadUInt32Big(buffer, offset);
        var lo = ReadUInt32Big(buffer, offset + 4);
        return ((ulong)hi << 32) | lo;
    }

    public static byte[] ReadBytes(IBlockReader reader, long offset, int count)
    {
        var result = new byte[count];
        reader.ReadAt(offset, result, 0, count);
        return result;
    }

    public static string ReadAscii(byte[] buffer, int offset, int count)
    {
        return Encoding.ASCII.GetString(buffer, offset, count).TrimEnd('\0', ' ');
    }

    public static string ReadUtf16LeZ(byte[] buffer, int offset, int bytes)
    {
        var end = offset;
        var max = offset + bytes;
        while (end + 1 < max)
        {
            if (buffer[end] == 0 && buffer[end + 1] == 0)
            {
                break;
            }

            end += 2;
        }

        return Encoding.Unicode.GetString(buffer, offset, end - offset);
    }

    public static long SignExtend(ulong value, int byteCount)
    {
        if (byteCount <= 0)
        {
            return 0;
        }

        var bits = byteCount * 8;
        var signBit = 1UL << (bits - 1);
        var mask = bits == 64 ? ulong.MaxValue : ((1UL << bits) - 1);
        value &= mask;
        if ((value & signBit) == 0)
        {
            return (long)value;
        }

        return unchecked((long)(value | ~mask));
    }
}
