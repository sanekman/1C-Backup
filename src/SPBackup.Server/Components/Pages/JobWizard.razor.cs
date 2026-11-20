using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;

namespace SPBackup.Server.Components.Pages;

public partial class JobWizard
{
    [Parameter] public Guid Id { get; set; }
    [Parameter] public Guid? JobId { get; set; }
    [Parameter] public Guid? TemplateId { get; set; }
    [SupplyParameterFromForm] public FileJobDraft? Model { get; set; }
    [SupplyParameterFromForm] public string? Command { get; set; }
    private AgentRecord? Agent;
    private ManagedFileConfiguration? Configuration;
    private ManagedFileConfiguration? Saved;
    private string? Error;
    private IReadOnlyList<string> Errors = [];
    private bool Conflict;
    private static readonly string[] Steps = ["Источник", "Расписание", "Хранилище", "Проверка"];

    protected override async Task OnParametersSetAsync()
    {
        var context = Http.HttpContext!;
        Agent = await Store.AgentAsync(Id, context.RequestAborted);
        if (Agent is null) { Error = "Агент не найден."; context.Response.StatusCode = 404; return; }
        if (Agent.Revoked) { Error = "Доступ агента отозван. Сначала подключите агент заново."; return; }
        try { Configuration = await Store.ReadFileConfigurationAsync(Id, context.RequestAborted); }
        catch (ServerRequestException) { Error = "Этот план содержит параметры, которые мастер пока не поддерживает. План сохранён без изменений."; return; }
        var selectedId = JobId ?? TemplateId;
        var job = selectedId is { } jobId ? Configuration.Jobs.FirstOrDefault(j => j.Id == jobId) : null;
        if (selectedId is not null && job is null)
        { Error = "Задание не найдено. Возможно, другой администратор уже удалил его."; Configuration = null; context.Response.StatusCode = 404; return; }
        Model ??= job is null ? new() { Revision = Configuration.Revision.ToString(CultureInfo.InvariantCulture) }
            : TemplateId is null ? FileJobDraft.From(job, Configuration.Revision)
            : FileJobDraft.CopyFrom(job, Configuration.Revision);
    }

    private async Task Submit()
    {
        if (Configuration is null || Agent is not { Revoked: false } || Model is null) return;
        var context = Http.HttpContext!;
        if (Model.Step is < 1 or > 4 || !Guid.TryParse(Model.JobId, out var jobId) || jobId == Guid.Empty
            || JobId is { } routeId && routeId != jobId || !long.TryParse(Model.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
        { Model.Step = 1; Error = "Не удалось прочитать форму. Откройте мастер заново из списка заданий."; return; }
        if (revision != Configuration.Revision)
        { ShowConflict(); return; }
        if (Model.Step == 1 || Command == "publish") Model.UpdateSuggestedName();
        if (Command == "back") { Model.Step = Math.Max(1, Model.Step - 1); return; }
        if (Model.Step == 3 && Command == "add-storage")
        {
            if (Model.AdditionalDestinations.Count >= FileJobDraft.MaxAdditionalDestinations)
                Error = "Уже добавлено максимальное число хранилищ: 8.";
            else Model.AdditionalDestinations.Add(new());
            return;
        }
        if (Model.Step == 3 && Command?.StartsWith("remove-storage:", StringComparison.Ordinal) == true)
        {
            if (int.TryParse(Command["remove-storage:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < Model.AdditionalDestinations.Count) Model.AdditionalDestinations.RemoveAt(index);
            else Error = "Не удалось выбрать хранилище для удаления из формы. Изменения плана не сохранены.";
            return;
        }
        if (Command == "next" && Model.Step < 4)
        {
            Errors = Model.ValidateStep(Model.Step);
            if (Errors.Count == 0) Model.Step++;
            return;
        }
        if (Command != "publish" || Model.Step != 4) { Error = "Пройдите шаги мастера перед сохранением."; return; }
        for (var step = 1; step <= 3; step++)
        {
            Errors = Model.ValidateStep(step);
            if (Errors.Count > 0) { Model.Step = step; return; }
        }
        if (JobId is null && Configuration.Jobs.Any(j => j.Id == jobId))
        { Error = "Задание с этим ID уже существует. Откройте новое задание из списка."; return; }
        if (!Guid.TryParse(context.User.FindFirstValue("stamp"), out var stamp))
        { Error = "Сеанс завершён. Войдите в панель заново."; return; }
        try
        {
            var edited = Model.Build();
            var jobs = JobId is null ? Configuration.Jobs.Append(edited).ToArray()
                : Configuration.Jobs.Select(j => j.Id == jobId ? edited : j).ToArray();
            Saved = await Store.PublishFileConfigurationAsync(ServerAuthentication.UserId(context.User), stamp, Id,
                revision, new(Id, 1, jobs), context.RequestAborted);
        }
        catch (ServerRequestException e) when (e.Status == 412) { ShowConflict(); }
        catch (ServerRequestException e)
        {
            Error = e.Code switch { "invalid_session" => "Сеанс завершён. Войдите в панель заново.",
                "agent_revoked" => "Доступ агента отозван. Изменения не сохранены.",
                "invalid_file_configuration" => "Не удалось принять настройки. Проверьте пути, расписание и лимит заданий (не больше 256).",
                _ => "Изменения не сохранены. Обновите список заданий и проверьте доступ к агенту." };
        }
    }
    private void ShowConflict()
    {
        Conflict = true;
        Error = "План агента изменён в другой вкладке или другим администратором. Ваши изменения не сохранены.";
        Http.HttpContext!.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
    }
    private static IReadOnlyDictionary<string, string> Zones(string current)
    {
        var zones = new Dictionary<string, string> { ["UTC"] = "UTC", ["Europe/Kaliningrad"] = "Калининград (UTC+2)",
            ["Europe/Moscow"] = "Москва (UTC+3)", ["Europe/Samara"] = "Самара (UTC+4)", ["Asia/Yekaterinburg"] = "Екатеринбург (UTC+5)",
            ["Asia/Omsk"] = "Омск (UTC+6)", ["Asia/Krasnoyarsk"] = "Красноярск (UTC+7)", ["Asia/Irkutsk"] = "Иркутск (UTC+8)",
            ["Asia/Chita"] = "Чита (UTC+9)", ["Asia/Vladivostok"] = "Владивосток (UTC+10)", ["Asia/Magadan"] = "Магадан (UTC+11)",
            ["Asia/Kamchatka"] = "Камчатка (UTC+12)" };
        if (current is { Length: > 0 } && current != "Local" && !zones.ContainsKey(current)) zones.Add(current, current);
        return zones;
    }
    private static string ZoneLabel(string zone) => zone == "Local" ? "Время компьютера агента"
        : zone is { Length: > 0 } ? Zones(zone)[zone] : "Выберите часовой пояс";
}
