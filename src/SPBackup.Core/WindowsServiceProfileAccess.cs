using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SPBackup.Core;

[SupportedOSPlatform("windows")]
public static class WindowsServiceProfileAccess
{
    public static void Grant(string directory, WindowsServiceProfile profile)
    {
        profile.Validate();
        if (profile.EffectiveServiceSid == profile.OwnerSid) return;
        if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
            Path.Combine(profile.LocalAppData, "OneCBackup").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Служба с другим пользователем использует стандартный рабочий профиль OneCBackup владельца заданий.");
        var folder = new DirectoryInfo(directory);
        // Never follow a junction while changing permissions as an administrator.
        for (DirectoryInfo? current = folder; current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Рабочий профиль службы не должен проходить через ссылки или junction.");
        var sid = new SecurityIdentifier(profile.EffectiveServiceSid);
        var acl = folder.GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier))?.Value != profile.OwnerSid)
            throw new UnauthorizedAccessException("Рабочая папка должна принадлежать владельцу заданий. Проверьте владельца папки OneCBackup в свойствах безопасности Windows.");
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(acl);
        // libpq must read the owner's configured password file; grant only this file,
        // never the whole roaming profile. New/replaced password files need their ACL reapplied.
        var pgpass = new FileInfo(Path.Combine(profile.AppData, "postgresql", "pgpass.conf"));
        if (pgpass.Exists)
        {
            for (DirectoryInfo? parent = pgpass.Directory; parent is not null; parent = parent.Parent)
                if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Файл паролей PostgreSQL не должен проходить через ссылки.");
            if (pgpass.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Файл паролей PostgreSQL не должен быть ссылкой.");
            var fileAcl = pgpass.GetAccessControl();
            if (fileAcl.GetOwner(typeof(SecurityIdentifier))?.Value != profile.OwnerSid)
                throw new UnauthorizedAccessException("Файл pgpass.conf принадлежит другому пользователю. Настройте доступ выбранной учётной записи вручную.");
            fileAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
            pgpass.SetAccessControl(fileAcl);
        }
    }
}
