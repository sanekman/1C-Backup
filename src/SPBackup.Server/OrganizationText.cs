namespace SPBackup.Server;

public static class OrganizationText
{
    public static string Error(ServerRequestException error) => error.Code switch
    {
        "folder_name_exists" => "В выбранной папке уже есть подпапка с таким именем. Выберите её в списке или измените имя новой папки.",
        "invalid_folder_name" => "Введите имя папки от 1 до 128 символов, без слешей и управляющих символов.",
        "invalid_agent_name" or "invalid_name_encoding" => "Введите понятное имя агента от 1 до 128 символов без управляющих символов.",
        "folder_not_found" => "Выбранная папка больше не существует. Обновите страницу и выберите другую.",
        "folder_not_empty" => "Папка содержит агентов, подпапки или действующие приглашения. Сначала перенесите агентов и подпапки; для приглашений дождитесь окончания срока действия.",
        "folder_cycle" => "Нельзя поместить папку внутрь неё самой или её подпапки.",
        "folder_depth_limit" => "Допускается до 16 уровней папок. Выберите родительскую папку ближе к корню.",
        "folder_limit" => "На сервере уже создано 1000 папок. Используйте существующую папку.",
        _ => "Не удалось сохранить изменение. Обновите страницу и проверьте выбранное имя и папку."
    };
    public static Guid? FolderId(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ServerRequestException(400, "folder_not_found");
    public static string? NewFolder(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
