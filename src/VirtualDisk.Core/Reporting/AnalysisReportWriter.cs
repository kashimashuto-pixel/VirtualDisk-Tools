using System.Text.Json;
using Qcow2Explorer.Core;
using Qcow2Explorer.Partitions;

namespace Qcow2Explorer.Reporting;

public static class AnalysisReportWriter
{
    public static void Write(
        string path,
        IDiskImageReader reader,
        IReadOnlyList<PartitionInfo> partitions,
        IReadOnlyList<string>? analysisWarnings = null)
    {
        var report = new
        {
            generatedUtc = DateTime.UtcNow,
            application = "Virtual Disk Explorer",
            source = reader.GetHeaderRows().ToDictionary(row => row.Key, row => row.Value),
            warnings = reader.GetWarnings().Concat(analysisWarnings ?? Array.Empty<string>()),
            partitions = partitions.Select(p => new
            {
                p.Number,
                p.Scheme,
                p.Name,
                p.Type,
                p.TypeId,
                p.FileSystem,
                p.StartLba,
                p.SectorCount,
                p.LengthBytes
            })
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
