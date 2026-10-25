using System.Text.Json;
namespace SPBackup.Core;

public sealed record LocalManualRunRequest(Guid RequestId, Guid SessionId, Guid JobId, DateTimeOffset SubmittedUtc);
public sealed record LocalManualRunHandle(Guid RequestId, Guid SessionId, Guid JobId);
public enum LocalManualRunPhase { Accepted, Finished, Rejected, Interrupted }
public sealed record LocalManualRunReceipt(Guid RequestId, Guid SessionId, Guid JobId, LocalManualRunPhase Phase, string? Error = null);

/// <summary>Commands refer only to saved job IDs and one live host session. Never replay a previous session.</summary>
public sealed class LocalManualRunQueue(string directory)
{
    private string PendingDirectory => Path.Combine(directory,"manual-runs","pending");
    private string Pending(Guid id) => Path.Combine(PendingDirectory,id.ToString("N")+".json");
    private string ReceiptDirectory => Path.Combine(directory,"manual-runs","receipts");
    private string Receipt(Guid id) => Path.Combine(ReceiptDirectory,id.ToString("N")+".json");
    public async Task<LocalManualRunRequest> EnqueueAsync(Guid sessionId,Guid jobId,CancellationToken token=default)
    {
        if(sessionId==Guid.Empty || jobId==Guid.Empty) throw new ArgumentException("Не указан сеанс агента или задание.");
        Directory.CreateDirectory(PendingDirectory);
        if(Directory.EnumerateFiles(PendingDirectory,"*.json").Take(1000).Count()>=1000) throw new InvalidOperationException("Очередь ручных запусков заполнена.");
        var request=new LocalManualRunRequest(Guid.NewGuid(),sessionId,jobId,DateTimeOffset.UtcNow);
        await new AgentJsonFile<LocalManualRunRequest>(Pending(request.RequestId)).WriteAsync(request,token);
        return request;
    }
    public async Task<IReadOnlyList<LocalManualRunRequest>> PendingAsync(CancellationToken token=default)
    {
        if(!Directory.Exists(PendingDirectory)) return [];
        var result=new List<LocalManualRunRequest>();
        foreach(var file in Directory.EnumerateFiles(PendingDirectory,"*.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if(new FileInfo(file).Length>8192) continue;
                var value=await new AgentJsonFile<LocalManualRunRequest>(file).ReadAsync(token);
                if(value is not null && value.RequestId!=Guid.Empty && value.SessionId!=Guid.Empty && value.JobId!=Guid.Empty
                    && value.SubmittedUtc!=default && Path.GetFileName(file)==value.RequestId.ToString("N")+".json") result.Add(value);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result.OrderBy(r=>r.SubmittedUtc).ThenBy(r=>r.RequestId).ToArray();
    }
    /// <summary>Recover live requests after reopening the window, including commands consumed by the host.</summary>
    public async Task<IReadOnlyList<LocalManualRunHandle>> OutstandingAsync(Guid sessionId,CancellationToken token=default)
    {
        if(sessionId==Guid.Empty) throw new ArgumentException("Не указан сеанс агента.");
        var result=(await PendingAsync(token)).Where(r=>r.SessionId==sessionId)
            .ToDictionary(r=>r.RequestId,r=>new LocalManualRunHandle(r.RequestId,r.SessionId,r.JobId));
        if(!Directory.Exists(ReceiptDirectory)) return result.Values.ToArray();
        foreach(var file in Directory.EnumerateFiles(ReceiptDirectory,"*.json"))
        {
            token.ThrowIfCancellationRequested();
            if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out var id)) continue;
            try
            {
                var receipt=await ReadReceiptAsync(id,token);
                if(receipt is { Phase: LocalManualRunPhase.Accepted } && receipt.SessionId==sessionId)
                    result[id]=new(id,sessionId,receipt.JobId);
                else if(receipt is not null) result.Remove(id);
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result.Values.ToArray();
    }
    public async Task<LocalManualRunReceipt?> ReadReceiptAsync(Guid id,CancellationToken token=default)
    {
        var path=Receipt(id);
        if(File.Exists(path) && new FileInfo(path).Length>8192) throw new InvalidDataException("Подтверждение команды слишком велико.");
        var value=await new AgentJsonFile<LocalManualRunReceipt>(path).ReadAsync(token);
        if(value is not null && (value.RequestId!=id || value.SessionId==Guid.Empty || value.JobId==Guid.Empty || !Enum.IsDefined(value.Phase)))
            throw new InvalidDataException("Некорректное подтверждение ручного запуска.");
        return value;
    }
    public Task WriteReceiptAsync(LocalManualRunReceipt value,CancellationToken token=default) => new AgentJsonFile<LocalManualRunReceipt>(Receipt(value.RequestId)).WriteAsync(value,token);
    public void Consume(Guid id) => File.Delete(Pending(id));
}
