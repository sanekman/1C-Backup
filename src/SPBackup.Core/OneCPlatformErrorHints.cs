using System.Text;

namespace SPBackup.Core;

internal static class OneCPlatformErrorHints
{
    public static string? Read(string path)
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite | FileShare.Delete);
            if(stream.Length>1024*1024)return null;
            var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
            var texts=new List<string> { Encoding.UTF8.GetString(bytes),Encoding.Unicode.GetString(bytes) };
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            texts.Add(Encoding.GetEncoding(1251).GetString(bytes));
            foreach(var text in texts)
            {
                var value=text.ToLowerInvariant();
                if(value.Contains("пользователь иб не идентифицирован") || value.Contains("пользователь информационной базы не идентифицирован")
                    || value.Contains("неверное имя или пароль") || value.Contains("неверное имя пользователя или пароль"))
                    return "Не выполнен вход пользователя ИБ 1С. Задайте пользователя и пароль базы в настройках выгрузки DT; проверьте право этого пользователя на выгрузку.";
                if(value.Contains("не обнаружена лицензия") || value.Contains("не найдена лицензия") || value.Contains("не найден ключ защиты"))
                    return "Платформе 1С недоступна лицензия. Проверьте лицензию под учётной записью исполнителя.";
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
