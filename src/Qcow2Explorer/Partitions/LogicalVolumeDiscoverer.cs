using DiscUtils;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.Partitions;

public static class LogicalVolumeDiscoverer
{
    private static int _registered;

    public static IReadOnlyList<PartitionInfo> Discover(IBlockReader disk, int firstNumber, List<IDisposable> ownedReaders)
    {
        EnsureRegistered();

        try
        {
            var manager = new VolumeManager(new BlockReaderStream(disk));
            var result = new List<PartitionInfo>();
            var number = firstNumber;
            foreach (var volume in manager.GetLogicalVolumes())
            {
                var identity = volume.Identity ?? "";
                var type = volume.TypeAsString ?? "LVM logical volume";
                if (!IsLvmVolume(volume))
                {
                    continue;
                }

                var stream = volume.Open();
                var reader = new StreamBlockReader(stream);
                ownedReaders.Add(reader);
                result.Add(new PartitionInfo
                {
                    Number = number++,
                    Scheme = "LVM2",
                    Name = ShortenIdentity(identity),
                    Type = type,
                    TypeId = identity,
                    StartLba = 0,
                    SectorCount = checked((ulong)Math.Max(0, volume.Length / 512)),
                    ReaderOverride = reader,
                    LengthOverrideBytes = volume.Length
                });
            }

            return result;
        }
        catch
        {
            return Array.Empty<PartitionInfo>();
        }
    }

    private static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        VolumeManager.RegisterLogicalVolumeFactory(typeof(DiscUtils.Lvm.LogicalVolumeManager).Assembly);
    }

    private static bool IsLvmVolume(LogicalVolumeInfo volume)
    {
        var type = volume.TypeAsString ?? "";
        var identity = volume.Identity ?? "";
        return type.Contains("LVM", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Logical Volume", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("LVM", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortenIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return "LVM logical volume";
        }

        return identity.Length <= 48 ? identity : identity[..45] + "...";
    }
}
