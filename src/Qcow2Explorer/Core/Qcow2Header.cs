namespace Qcow2Explorer.Core;

public sealed class Qcow2Header
{
    public const uint MagicValue = 0x514649fb;

    public uint Magic { get; init; }
    public uint Version { get; init; }
    public ulong BackingFileOffset { get; init; }
    public uint BackingFileSize { get; init; }
    public uint ClusterBits { get; init; }
    public ulong VirtualSize { get; init; }
    public uint CryptMethod { get; init; }
    public uint L1Size { get; init; }
    public ulong L1TableOffset { get; init; }
    public ulong RefcountTableOffset { get; init; }
    public uint RefcountTableClusters { get; init; }
    public uint SnapshotCount { get; init; }
    public ulong SnapshotsOffset { get; init; }
    public ulong IncompatibleFeatures { get; init; }
    public ulong CompatibleFeatures { get; init; }
    public ulong AutoClearFeatures { get; init; }
    public uint RefcountOrder { get; init; }
    public uint HeaderLength { get; init; }
    public byte CompressionType { get; init; }
    public string? BackingFileName { get; init; }

    public long ClusterSize => 1L << (int)ClusterBits;
    public bool HasBackingFile => BackingFileOffset != 0 && BackingFileSize != 0;
    public bool IsEncrypted => CryptMethod != 0;
    public bool IsDirty => (IncompatibleFeatures & 0x1) != 0;
    public bool IsMarkedCorrupt => (IncompatibleFeatures & 0x2) != 0;
    public bool UsesExternalDataFile => (IncompatibleFeatures & 0x4) != 0;
    public bool UsesNonDefaultCompression => (IncompatibleFeatures & 0x8) != 0;
    public bool UsesExtendedL2Entries => (IncompatibleFeatures & 0x10) != 0;
    public ulong UnknownIncompatibleFeatures => IncompatibleFeatures & ~0x1fUL;

    public static Qcow2Header Parse(FileStream stream)
    {
        var headerBuffer = new byte[112];
        stream.Position = 0;
        ReadExact(stream, headerBuffer, 0, 104);

        var magic = EndianUtilities.ReadUInt32Big(headerBuffer, 0);
        if (magic != MagicValue)
        {
            throw new InvalidDataException("qcow2 magic が一致しません。");
        }

        var version = EndianUtilities.ReadUInt32Big(headerBuffer, 4);
        if (version != 2 && version != 3)
        {
            throw new NotSupportedException($"qcow2 version {version} は未対応です。");
        }

        var headerLength = version == 2 ? 72U : EndianUtilities.ReadUInt32Big(headerBuffer, 100);
        if (version == 3 && headerLength >= 112)
        {
            ReadExact(stream, headerBuffer, 104, 8);
        }

        var backingOffset = EndianUtilities.ReadUInt64Big(headerBuffer, 8);
        var backingSize = EndianUtilities.ReadUInt32Big(headerBuffer, 16);
        string? backingName = null;
        if (backingOffset > 0 && backingSize > 0 && backingSize <= 1023)
        {
            var nameBuffer = new byte[backingSize];
            stream.Position = (long)backingOffset;
            ReadExact(stream, nameBuffer, 0, nameBuffer.Length);
            backingName = System.Text.Encoding.UTF8.GetString(nameBuffer);
        }

        var clusterBits = EndianUtilities.ReadUInt32Big(headerBuffer, 20);
        if (clusterBits is < 9 or > 31)
        {
            throw new NotSupportedException($"cluster_bits={clusterBits} は未対応です。");
        }

        return new Qcow2Header
        {
            Magic = magic,
            Version = version,
            BackingFileOffset = backingOffset,
            BackingFileSize = backingSize,
            ClusterBits = clusterBits,
            VirtualSize = EndianUtilities.ReadUInt64Big(headerBuffer, 24),
            CryptMethod = EndianUtilities.ReadUInt32Big(headerBuffer, 32),
            L1Size = EndianUtilities.ReadUInt32Big(headerBuffer, 36),
            L1TableOffset = EndianUtilities.ReadUInt64Big(headerBuffer, 40),
            RefcountTableOffset = EndianUtilities.ReadUInt64Big(headerBuffer, 48),
            RefcountTableClusters = EndianUtilities.ReadUInt32Big(headerBuffer, 56),
            SnapshotCount = EndianUtilities.ReadUInt32Big(headerBuffer, 60),
            SnapshotsOffset = EndianUtilities.ReadUInt64Big(headerBuffer, 64),
            IncompatibleFeatures = version == 3 ? EndianUtilities.ReadUInt64Big(headerBuffer, 72) : 0,
            CompatibleFeatures = version == 3 ? EndianUtilities.ReadUInt64Big(headerBuffer, 80) : 0,
            AutoClearFeatures = version == 3 ? EndianUtilities.ReadUInt64Big(headerBuffer, 88) : 0,
            RefcountOrder = version == 3 ? EndianUtilities.ReadUInt32Big(headerBuffer, 96) : 4,
            HeaderLength = headerLength,
            CompressionType = version == 3 && headerLength > 104 ? headerBuffer[104] : (byte)0,
            BackingFileName = backingName
        };
    }

    public IReadOnlyList<string> GetReadWarnings()
    {
        var warnings = new List<string>();
        if (IsEncrypted)
        {
            warnings.Add($"暗号化 qcow2 は未対応です: crypt_method={CryptMethod}");
        }

        if (UsesExternalDataFile)
        {
            warnings.Add("外部 data file を使う qcow2 は、この版では未対応です。");
        }

        if (UsesExtendedL2Entries)
        {
            warnings.Add("Extended L2 Entries を使う qcow2 は、この版では未対応です。");
        }

        if (UnknownIncompatibleFeatures != 0)
        {
            warnings.Add($"未知の incompatible feature があります: 0x{UnknownIncompatibleFeatures:X}");
        }

        if (HasBackingFile)
        {
            warnings.Add($"backing file があります。未割り当て領域は backing file ではなく 0 として読みます: {BackingFileName}");
        }

        if (IsDirty)
        {
            warnings.Add("Dirty bit が立っています。読み取りは試行しますが、メタデータの整合性に注意してください。");
        }

        if (IsMarkedCorrupt)
        {
            warnings.Add("Corrupt bit が立っています。表示結果が正しいとは限りません。");
        }

        if (UsesNonDefaultCompression)
        {
            warnings.Add($"非標準圧縮が指定されています。この版では圧縮クラスタの展開は未対応です: compression_type={CompressionType}");
        }

        return warnings;
    }

    public bool CanReadStandardClusters =>
        !IsEncrypted
        && !UsesExternalDataFile
        && !UsesExtendedL2Entries
        && UnknownIncompatibleFeatures == 0;

    private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var readTotal = 0;
        while (readTotal < count)
        {
            var read = stream.Read(buffer, offset + readTotal, count - readTotal);
            if (read == 0)
            {
                throw new EndOfStreamException("qcow2 ヘッダーの読み込み中にファイル終端へ到達しました。");
            }

            readTotal += read;
        }
    }
}
