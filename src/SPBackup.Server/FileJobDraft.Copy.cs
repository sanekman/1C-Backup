namespace SPBackup.Server;

public sealed partial class FileJobDraft
{
    public static FileJobDraft CopyFrom(ManagedFileJob job, long revision)
    {
        var draft = From(job, revision);
        draft.JobId = Guid.NewGuid().ToString();
        draft.Enabled = false;
        draft.Name = "";
        draft.UpdateSuggestedName();
        return draft;
    }

    public void UpdateSuggestedName()
    {
        // A custom name survives source changes. The last suggestion is carried through
        // the form so back navigation can update automatic names without JavaScript.
        if (!string.IsNullOrWhiteSpace(Name) && Name != SuggestedName) return;
        if (!FileConfigurationContract.WindowsPath(Source?.Trim())) return;
        var path = Source!.Trim().Replace('/', '\\').TrimEnd('\\');
        if (Kind == "OneCFile" && path.EndsWith(@"\1Cv8.1CD", StringComparison.OrdinalIgnoreCase))
            path = path[..path.LastIndexOf('\\')];
        var leaf = path[(path.LastIndexOf('\\') + 1)..];
        SuggestedName = Name = leaf.Length == 2 && leaf[1] == ':' ? $"Диск {char.ToUpperInvariant(leaf[0])}" : leaf;
    }
}
