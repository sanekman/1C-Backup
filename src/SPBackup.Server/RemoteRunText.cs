namespace SPBackup.Server;
public static class RemoteRunText
{
    public static string Status(string status) => status switch {
        "Queued" => "Ожидает агента", "Accepted" => "Принята, ожидает исполнителя", "Running" => "Выполняется",
        "Succeeded" => "Копирование завершено", "Failed" => "Копирование завершилось с ошибкой или отменой",
        "Rejected" => "Отклонена", "Expired" => "Срок ожидания истёк", "Interrupted" => "Прервана, проверьте историю", _ => "Нет данных" };
    public static string Reason(string reason) => reason switch {
        "JobUnavailable" => "Задание или подключение недоступны.", "RevisionChanged" => "План изменился до запуска.",
        "AgentRestarted" => "Сеанс агента завершился до подтверждения результата.", "DeadlineElapsed" => "Истёк срок запуска команды.",
        _ => "Результат запуска не подтверждён. Проверьте историю на агенте." };
}
