namespace SPBackup.Server;

public sealed record ManagedPlanStatus(long SavedRevision, long? AcceptedRevision, DateTimeOffset? AcceptedUtc,
    string? Decision, string? RejectionReason, long? ReportedRevision, DateTimeOffset? ReportedUtc,
    bool? ExecutorReady, DateTimeOffset? ExecutorObservedUtc, long? AccessRevision, DateTimeOffset? AccessUtc,
    int CheckedAccess, int MissingAccess, DateTimeOffset Cutoff)
{
    internal bool Rejected => Decision == "Rejected";
    internal bool RevisionDiffers => SavedRevision != AcceptedRevision || ReportedRevision != SavedRevision;
    internal bool ReportStale => ReportedUtc < Cutoff;
    internal bool AccessStale => AccessUtc < Cutoff;
    internal bool NeedsAttention => Rejected || RevisionDiffers || ReportStale || ExecutorReady != true || AccessStale || MissingAccess > 0;

    internal string PlanLabel => AcceptedRevision is { } revision
        ? $"План {SavedRevision} · принят {revision}"
        : $"План {SavedRevision} · без подтверждения";
    internal string DecisionLabel => Decision switch
    {
        "Accepted" => "Агент подтвердил проверку и сохранение плана",
        "Rejected" when RejectionReason == "CacheWriteFailed" => "Агент не смог сохранить настройки",
        "Rejected" => "Агент отклонил настройки",
        _ => "Ожидается подтверждение агента"
    };
    internal string ExecutorLabel => ExecutorReady switch
    {
        true => "Исполнитель готов",
        false => "Исполнитель недоступен",
        null when ExecutorObservedUtc is not null => "Сигнал исполнителя устарел",
        _ => "Нет сигнала исполнителя"
    };
    internal string AccessLabel => (CheckedAccess, MissingAccess) switch
    {
        (_, > 0) => $"Не задан доступ для заданий: {MissingAccess}",
        (> 0, _) => $"Настроены привязки для заданий: {CheckedAccess}",
        _ => "Нет сведений о привязках"
    };
}
