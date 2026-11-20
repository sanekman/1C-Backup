using System.Diagnostics;
using System.Text.Json;
using SPBackup.Core;
using Xunit;
using Xunit.Abstractions;

namespace SPBackup.Core.Tests;

public sealed class HistoryPerformanceTests(ITestOutputHelper output)
{
    [Fact, Trait("Category", "Performance")]
    public async Task MeasureHistoryReadStartAndDiagnostics()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sp-history-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var started = DateTimeOffset.UtcNow.AddYears(-1);
            var job = Guid.NewGuid();
            var profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            var index = Path.Combine(profile, "runs.json");
            var history = new BackupRunHistory(index);
            using var log = DiagnosticLog.Use(new DiagnosticLog(profile, DiagnosticComponent.Tests, "benchmark", "test"));
            foreach (var count in new[] { 7300, 10950 })
            {
                var runs = Enumerable.Range(0, count).Select(item => new BackupRun(Guid.NewGuid(), job, "Test files",
                    started.AddMinutes(item), started.AddMinutes(item + 1), BackupRunStatus.Succeeded, "",
                    new("sample.zip", 1024, new string('a', 64)))).ToArray();
                await File.WriteAllTextAsync(index, JsonSerializer.Serialize(runs));
                var read = new List<double>();
                var save = new List<double>();
                var export = new List<double>();
                var allocated = GC.GetTotalAllocatedBytes();
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var start = Stopwatch.GetTimestamp();
                    Assert.InRange((await history.ReadAsync()).Count, count, count + 1);
                    if (attempt != 0) read.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = Stopwatch.GetTimestamp();
                    await history.SaveAsync(new(job, job, "Test files", DateTimeOffset.UtcNow, null, BackupRunStatus.Running, ""));
                    if (attempt != 0) save.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    start = Stopwatch.GetTimestamp();
                    var archive = Path.Combine(root, "support.zip");
                    await new DiagnosticBundle(profile, "benchmark", "test").ExportAsync(archive);
                    if (attempt != 0) export.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    File.Delete(archive);
                }
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    runs = count, readMs = Percentiles(read), saveRunningRecordMs = Percentiles(save), diagnosticsMs = Percentiles(export),
                    historyBytes = new FileInfo(index).Length, allocatedBytesInProcess = GC.GetTotalAllocatedBytes() - allocated,
                    processWorkingSetBytes = Environment.WorkingSet
                }));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static double[] Percentiles(List<double> values)
    {
        values.Sort();
        return [Math.Round(values[values.Count / 2], 2), Math.Round(values[(int)Math.Ceiling(values.Count * .95) - 1], 2)];
    }
}
