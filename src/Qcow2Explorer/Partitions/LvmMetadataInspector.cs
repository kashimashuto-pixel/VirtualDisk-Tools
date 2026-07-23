using System.Text;
using System.Text.RegularExpressions;
using Qcow2Explorer.Core;
using Qcow2Explorer.FileSystems;

namespace Qcow2Explorer.Partitions;

public static partial class LvmMetadataInspector
{
    private const int MaximumScanBytes = 8 * 1024 * 1024;

    public static LvmMetadataInspectionResult Inspect(
        IBlockReader disk,
        IReadOnlyList<PartitionInfo> partitions)
    {
        var summaries = new List<LvmMetadataSummary>();
        var errors = new List<string>();
        foreach (var partition in partitions)
        {
            try
            {
                var reader = new PartitionSliceReader(partition.ReaderOverride ?? disk, partition);
                var length = checked((int)Math.Min(reader.Length, MaximumScanBytes));
                var offsets = new[] { 0L, Math.Max(0, reader.Length - length) }.Distinct();
                foreach (var offset in offsets)
                {
                    var data = EndianUtilities.ReadBytes(reader, offset, length);
                    var metadata = ExtractMetadata(Encoding.ASCII.GetString(data));
                    if (metadata is null)
                    {
                        continue;
                    }

                    summaries.Add(Summarize(partition.Number, metadata));
                    break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"LVM2 PV #{partition.Number}: メタデータ候補領域を読み取れませんでした: {ex.Message}");
            }
        }

        return new LvmMetadataInspectionResult(summaries, errors);
    }

    public static LvmMetadataSummary Summarize(int partitionNumber, string metadata)
    {
        var physicalSection = ExtractSection(metadata, "physical_volumes") ?? "";
        var logicalSection = ExtractSection(metadata, "logical_volumes") ?? "";
        var segmentTypes = SegmentTypeRegex().Matches(logicalSection)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stripeCounts = StripeCountRegex().Matches(logicalSection)
            .Select(match => ulong.TryParse(match.Groups[1].Value, out var value) ? value : 0)
            .ToList();

        return new LvmMetadataSummary(
            partitionNumber,
            CountAssignments(physicalSection, "id"),
            CountAssignments(logicalSection, "segment_count"),
            segmentTypes,
            stripeCounts.Count == 0 ? 0 : stripeCounts.Max());
    }

    private static string? ExtractMetadata(string text)
    {
        var marker = ContentsRegex().Match(text);
        if (!marker.Success)
        {
            return null;
        }

        var start = text.LastIndexOf('\0', marker.Index);
        start = start < 0 ? 0 : start + 1;
        var end = text.IndexOf('\0', marker.Index);
        if (end < 0)
        {
            end = text.Length;
        }

        return text[start..end].Trim();
    }

    private static string? ExtractSection(string metadata, string name)
    {
        var match = Regex.Match(metadata, $@"(?m)^\s*{Regex.Escape(name)}\s*\{{");
        if (!match.Success)
        {
            return null;
        }

        var opening = metadata.IndexOf('{', match.Index);
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var index = opening; index < metadata.Length; index++)
        {
            var ch = metadata[index];
            if (quoted)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}' && --depth == 0)
            {
                return metadata[(opening + 1)..index];
            }
        }

        return null;
    }

    private static int CountAssignments(string section, string name)
    {
        return Regex.Matches(section, $@"(?m)^\s*{Regex.Escape(name)}\s*=").Count;
    }

    [GeneratedRegex(@"contents\s*=\s*""Text Format Volume Group""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentsRegex();

    [GeneratedRegex(@"(?m)^\s*type\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentTypeRegex();

    [GeneratedRegex(@"(?m)^\s*stripe_count\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StripeCountRegex();
}

public sealed record LvmMetadataSummary(
    int PartitionNumber,
    int PhysicalVolumeCount,
    int LogicalVolumeCount,
    IReadOnlyList<string> SegmentTypes,
    ulong MaximumStripeCount);

public sealed record LvmMetadataInspectionResult(
    IReadOnlyList<LvmMetadataSummary> Summaries,
    IReadOnlyList<string> Errors);
