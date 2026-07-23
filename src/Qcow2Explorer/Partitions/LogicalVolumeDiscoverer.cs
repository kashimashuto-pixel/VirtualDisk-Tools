using DiscUtils;
using DiscUtils.Lvm;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.Partitions;

public static class LogicalVolumeDiscoverer
{
    private static int _registered;

    public static LvmDiscoveryResult Discover(
        IBlockReader disk,
        IReadOnlyList<PartitionInfo> lvmPartitions,
        int firstNumber,
        List<IDisposable> ownedReaders)
    {
        EnsureRegistered();

        var volumes = new List<PartitionInfo>();
        var diagnostics = new List<LvmDiagnostic>();
        var diskStream = new BlockReaderStream(disk);
        var keepDiskStream = false;

        try
        {
            var metadataInspection = LvmMetadataInspector.Inspect(disk, lvmPartitions);
            var metadataSummaries = metadataInspection.Summaries;
            diagnostics.AddRange(metadataInspection.Errors.Select(error => new LvmDiagnostic(error, true)));
            AppendMetadataDiagnostics(metadataSummaries, lvmPartitions.Count, diagnostics);

            var manager = new VolumeManager(diskStream);
            var physicalVolumes = manager.GetPhysicalVolumes();
            var lvmPhysicalVolumes = physicalVolumes
                .Where(LogicalVolumeManager.HandlesPhysicalVolume)
                .ToList();
            diagnostics.Add(new LvmDiagnostic(
                $"LVM2: {lvmPhysicalVolumes.Count:N0}個のPhysical VolumeをDiscUtilsが認識しました。",
                false));

            var logicalVolumes = manager.GetLogicalVolumes()
                .Where(volume => volume.PhysicalVolume is null)
                .ToList();

            var number = firstNumber;
            foreach (var volume in logicalVolumes)
            {
                try
                {
                    var stream = volume.Open();
                    var reader = new StreamBlockReader(stream);
                    ownedReaders.Add(reader);
                    keepDiskStream = true;

                    var identity = volume.Identity ?? "";
                    volumes.Add(new PartitionInfo
                    {
                        Number = number++,
                        Scheme = "LVM2",
                        Name = ShortenIdentity(identity),
                        Type = "LVM2 logical volume",
                        TypeId = identity,
                        StartLba = 0,
                        SectorCount = checked((ulong)Math.Max(0, volume.Length / 512)),
                        ReaderOverride = reader,
                        LengthOverrideBytes = volume.Length
                    });
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new LvmDiagnostic(
                        $"LVM2 LV {volume.Identity}: 論理ボリュームを開けませんでした: {FormatException(ex)}",
                        true));
                }
            }

            if (volumes.Count == 0)
            {
                var metadataLvCount = metadataSummaries.Count == 0
                    ? 0
                    : metadataSummaries.Max(summary => summary.LogicalVolumeCount);
                var reason = metadataLvCount == 0
                    ? "LVMメタデータ内にLogical Volume定義がありません。"
                    : "Logical Volume定義はありますが、利用可能なPVと対応セグメントだけでは再構築できませんでした。";
                diagnostics.Add(new LvmDiagnostic($"LVM2: 読み取り可能なLogical Volumeは0個です。{reason}", true));
            }
            else
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2: {volumes.Count:N0}個のLogical Volumeを読み取り対象として追加しました。",
                    false));
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new LvmDiagnostic($"LVM2解析処理でエラーが発生しました: {FormatException(ex)}", true));
        }
        finally
        {
            if (keepDiskStream)
            {
                ownedReaders.Add(diskStream);
            }
            else
            {
                diskStream.Dispose();
            }
        }

        return new LvmDiscoveryResult(volumes, diagnostics);
    }

    private static void AppendMetadataDiagnostics(
        IReadOnlyList<LvmMetadataSummary> summaries,
        int availablePvCount,
        List<LvmDiagnostic> diagnostics)
    {
        if (summaries.Count == 0)
        {
            diagnostics.Add(new LvmDiagnostic(
                "LVM2ラベルは検出しましたが、先頭8 MiB内から有効なテキストメタデータを見つけられませんでした。メタデータ位置、CRC、または形式が未対応の可能性があります。",
                true));
            return;
        }

        foreach (var summary in summaries)
        {
            var types = summary.SegmentTypes.Count == 0
                ? "なし"
                : string.Join(", ", summary.SegmentTypes);
            diagnostics.Add(new LvmDiagnostic(
                $"LVM2 PV #{summary.PartitionNumber}: VGメタデータ検出、PV定義 {summary.PhysicalVolumeCount:N0}、LV定義 {summary.LogicalVolumeCount:N0}、segment={types}",
                false));

            if (summary.PhysicalVolumeCount > availablePvCount)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 PV #{summary.PartitionNumber}: VGは{summary.PhysicalVolumeCount:N0}個のPVを必要としますが、現在の入力から確認できるLVM2 PVは{availablePvCount:N0}個です。別ディスク上のPVが不足している可能性があります。",
                    true));
            }

            var unsupported = summary.SegmentTypes
                .Where(type => !string.Equals(type, "striped", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (unsupported.Count > 0)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 PV #{summary.PartitionNumber}: 未対応segment typeがあります: {string.Join(", ", unsupported)}。thin/snapshot/cache/mirror/RAID等は追加のマッピング処理が必要です。",
                    true));
            }

            if (summary.MaximumStripeCount > 1)
            {
                diagnostics.Add(new LvmDiagnostic(
                    $"LVM2 PV #{summary.PartitionNumber}: 最大stripe_count={summary.MaximumStripeCount:N0}です。現在のDiscUtilsは単一stripeのlinear相当のみを開けます。",
                    true));
            }
        }
    }

    private static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        VolumeManager.RegisterLogicalVolumeFactory(typeof(LogicalVolumeManager).Assembly);
    }

    private static string ShortenIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return "LVM logical volume";
        }

        return identity.Length <= 48 ? identity : identity[..45] + "...";
    }

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message);
            }
        }

        return string.Join(" -> ", messages);
    }
}

public sealed record LvmDiscoveryResult(
    IReadOnlyList<PartitionInfo> Volumes,
    IReadOnlyList<LvmDiagnostic> Diagnostics);

public sealed record LvmDiagnostic(string Message, bool IsError);
