using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

internal static class EwfTestImageFactory
{
    private const int FileHeaderSize = 13;
    private const int SectionDescriptorSize = 76;

    public static EwfTestFixture Create(
        string firstSegmentPath,
        byte[] raw,
        int chunkSize,
        IReadOnlyList<int> chunksPerSegment,
        IReadOnlySet<int> compressedChunks)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (chunkSize <= 0 || chunkSize % 512 != 0 || raw.Length == 0 || raw.Length % chunkSize != 0)
        {
            throw new ArgumentException("Synthetic EWF geometry is invalid.");
        }

        var totalChunks = raw.Length / chunkSize;
        if (chunksPerSegment.Count == 0 || chunksPerSegment.Sum() != totalChunks
            || chunksPerSegment.Any(count => count <= 0))
        {
            throw new ArgumentException("Synthetic EWF segment chunk counts are invalid.");
        }

        var directory = Path.GetDirectoryName(firstSegmentPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(firstSegmentPath);
        Directory.CreateDirectory(directory);
        var segmentPaths = new List<string>();
        var globalChunkIndex = 0;
        var uncompressedChecksumOffset = -1;

        for (var segmentIndex = 0; segmentIndex < chunksPerSegment.Count; segmentIndex++)
        {
            var extension = $".E{segmentIndex + 1:00}";
            var segmentPath = Path.Combine(directory, stem + extension);
            segmentPaths.Add(segmentPath);
            var output = new List<byte>();
            AppendFileHeader(output, checked((ushort)(segmentIndex + 1)));

            if (segmentIndex == 0)
            {
                var volume = new byte[1052];
                BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(4), checked((uint)totalChunks));
                BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(8), checked((uint)(chunkSize / 512)));
                BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(12), 512);
                BinaryPrimitives.WriteUInt64LittleEndian(volume.AsSpan(16), checked((ulong)(raw.Length / 512)));
                BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(1048), Adler32(volume.AsSpan(0, 1048)));
                AppendSection(output, "volume", volume);
            }

            var sectorsOffset = output.Count;
            var storedChunks = new List<byte[]>();
            var tableEntries = new List<uint>();
            var relativeOffset = SectionDescriptorSize;
            for (var localIndex = 0; localIndex < chunksPerSegment[segmentIndex]; localIndex++, globalChunkIndex++)
            {
                var chunk = raw.AsSpan(globalChunkIndex * chunkSize, chunkSize).ToArray();
                var compressed = compressedChunks.Contains(globalChunkIndex);
                byte[] stored;
                if (compressed)
                {
                    using var destination = new MemoryStream();
                    using (var zlib = new ZLibStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
                    {
                        zlib.Write(chunk);
                    }
                    stored = destination.ToArray();
                }
                else
                {
                    stored = new byte[chunk.Length + 4];
                    chunk.CopyTo(stored, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(chunk.Length), Adler32(chunk));
                    uncompressedChecksumOffset = sectorsOffset + relativeOffset + chunk.Length;
                }

                var entry = checked((uint)relativeOffset);
                tableEntries.Add(compressed ? entry | 0x80000000u : entry);
                storedChunks.Add(stored);
                relativeOffset = checked(relativeOffset + stored.Length);
            }

            AppendSection(output, "sectors", storedChunks.SelectMany(value => value).ToArray());
            var table = new byte[24 + tableEntries.Count * 4 + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(table, checked((uint)tableEntries.Count));
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(8), checked((ulong)sectorsOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(20), Adler32(table.AsSpan(0, 20)));
            for (var index = 0; index < tableEntries.Count; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24 + index * 4), tableEntries[index]);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                table.AsSpan(table.Length - 4),
                Adler32(table.AsSpan(24, tableEntries.Count * 4)));
            AppendSection(output, "table", table);
            AppendTerminalSection(output, segmentIndex + 1 == chunksPerSegment.Count ? "done" : "next");
            File.WriteAllBytes(segmentPath, output.ToArray());
        }

        if (uncompressedChecksumOffset < 0)
        {
            throw new ArgumentException("At least one uncompressed test chunk is required.");
        }
        return new EwfTestFixture(segmentPaths, uncompressedChecksumOffset);
    }

    private static void AppendFileHeader(List<byte> output, ushort segmentNumber)
    {
        output.AddRange([(byte)'E', (byte)'V', (byte)'F', 0x09, 0x0d, 0x0a, 0xff, 0x00, 1]);
        output.Add(checked((byte)(segmentNumber & 0xff)));
        output.Add(checked((byte)(segmentNumber >> 8)));
        output.Add(0);
        output.Add(0);
    }

    private static void AppendSection(List<byte> output, string type, byte[] data)
    {
        var offset = output.Count;
        var size = checked(SectionDescriptorSize + data.Length);
        output.AddRange(CreateDescriptor(type, checked((ulong)(offset + size)), checked((ulong)size)));
        output.AddRange(data);
    }

    private static void AppendTerminalSection(List<byte> output, string type)
    {
        var offset = output.Count;
        output.AddRange(CreateDescriptor(type, checked((ulong)offset), 0));
    }

    private static byte[] CreateDescriptor(string type, ulong nextOffset, ulong size)
    {
        var descriptor = new byte[SectionDescriptorSize];
        Encoding.ASCII.GetBytes(type).CopyTo(descriptor, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(16), nextOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(24), size);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(72), Adler32(descriptor.AsSpan(0, 72)));
        return descriptor;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }
}

internal sealed record EwfTestFixture(IReadOnlyList<string> SegmentPaths, int UncompressedChecksumOffset);
