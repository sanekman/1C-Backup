namespace SPBackup.Server;
public static class ProblemText
{
    public static int Priority(string code) => code switch { "Offline" => 0, "Failed" or "Interrupted" => 1, "Partial" => 2, "Incomplete" => 3, _ => 4 };
    public static IReadOnlyDictionary<string,string> Filters { get; } = new Dictionary<string,string> {
        ["all"] = "Все проблемы", ["Offline"] = "Нет связи", ["Unreported"] = "Нет отчётов", ["Failed"] = "Ошибки копирования",
        ["Cancelled"] = "Запуск отменён", ["Interrupted"] = "Запуск прерван",
        ["Partial"] = "Частичная доставка", ["Incomplete"] = "Неполные копии", ["Overdue"] = "Нет свежей копии", ["Warning"] = "Предупреждения",
        ["PollFailed"] = "Ошибка опроса SQL", ["AccessRequired"] = "Не задан доступ", ["PlanRejected"] = "План отклонён",
        ["PlanPending"] = "План ожидает применения", ["ExecutorUnavailable"] = "Исполнитель недоступен" };
    public static string Action(string code) => code switch {
        "Offline" => "Проверьте службу агента, сеть и HTTPS. Показанные результаты относятся к последнему полученному отчёту.",
        "Unreported" => "Дождитесь первого отчёта мониторинга; проверьте подключение Windows-агента.",
        "Overdue" => "Свежая успешная копия без пропусков не подтверждена. Проверьте расписание, историю и доставку.",
        "AccessRequired" => "В Windows-программе выберите задание и откройте «Файл → Доступ серверного задания…».",
        "PlanPending" => "Дождитесь связи и подтверждения ревизии агентом. Прежний принятый план продолжает работать.",
        "PlanRejected" => "Проверьте причину отказа в карточке агента и опубликуйте исправленный план. Прежний принятый план сохранён.",
        "ExecutorUnavailable" => "Проверьте режим и работу агента или службы на Windows-компьютере.",
        "PollFailed" => "Журнал внешних копий недоступен. Результат новых копий неизвестен.",
        "Interrupted" => "Проверьте доставленные копии перед повторным запуском.",
        "Cancelled" => "Откройте результат запуска, чтобы проверить доставленные копии.",
        _ => "Откройте результат задания: причины, пропуски и состояние каждого хранилища показаны отдельно." };
}
