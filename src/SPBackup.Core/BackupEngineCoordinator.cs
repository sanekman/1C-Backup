namespace SPBackup.Core;

public sealed class BackupEngineBusyException(Exception inner)
    : IOException("Другой исполнитель использует рабочий каталог. Остановите фоновый агент или планировщик.", inner);

/// <summary>One coordinator per host; exclusive file ownership across processes, counted within a host.</summary>
public sealed class BackupEngineCoordinator(string dataDirectory)
{
    private readonly object gate = new();
    private FileStream? stream;
    private int references;

    public IDisposable Acquire()
    {
        lock (gate)
        {
            if (stream is null)
            {
                Directory.CreateDirectory(dataDirectory);
                try { stream = new FileStream(Path.Combine(dataDirectory, "engine.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) { throw new BackupEngineBusyException(ex); }
            }
            references++;
            return new Lease(this);
        }
    }

    public bool IsHeldElsewhere()
    {
        lock (gate)
        {
            if (stream is not null) return false;
            try { using var lease = Acquire(); return false; }
            catch (BackupEngineBusyException) { return true; }
        }
    }

    private void Release()
    {
        lock (gate)
        {
            if (--references == 0) { stream!.Dispose(); stream = null; }
        }
    }

    private sealed class Lease(BackupEngineCoordinator owner) : IDisposable
    {
        private BackupEngineCoordinator? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Release();
    }
}
