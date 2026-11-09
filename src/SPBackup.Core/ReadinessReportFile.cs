using System.Text;

namespace SPBackup.Core;

public static class ReadinessReportFile
{
    public static async Task SaveNewAsync(string path, string text, CancellationToken token = default)
    {
        var target = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Выбранная папка не существует.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Файл или папка уже существует. Выберите другое имя.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        var owned = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                owned = true;
                await file.WriteAsync(Encoding.UTF8.GetBytes(text), token);
                await file.FlushAsync(token); file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            AtomicNewFile.Publish(temporary, target);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
}
