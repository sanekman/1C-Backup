using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OneCBackup.Core;

public sealed record WebDavEntry(string Path, bool IsDirectory, long Length, DateTimeOffset? ModifiedUtc, string? ETag);

/// <summary>Read-only DAV. Redirects are refused and credentials are sent only to the selected HTTPS origin.</summary>
public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient http;
    private readonly Uri root;
    private readonly AuthenticationHeaderValue authorization;
    public string SourceIdentity { get; }
    public WebDavClient(string source, NetworkCredential credentials, HttpMessageHandler? handler = null)
    {
        var errors = WebDavSettings.ValidateSource(source);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        if (string.IsNullOrWhiteSpace(credentials.UserName) || credentials.UserName.Contains(':')
            || credentials.UserName.Any(char.IsControl) || credentials.Password.Any(char.IsControl))
            throw new InvalidDataException("Некорректные учётные данные WebDAV.");
        root = new Uri(source.TrimEnd('/') + "/");
        authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        SourceIdentity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("webdav|" + root.AbsoluteUri + "|" + credentials.UserName)));
    }
    public static async Task<WebDavClient> OpenAsync(BackupJob job, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Учётные данные WebDAV доступны в Windows.");
        var credential = await new WindowsWebDavCredentialStore().GetAsync(job.WebDav!.CredentialKey, token);
        return new(job.Source, credential);
    }
    private HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = authorization;
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException ex)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.WebDavRequestFailed, DiagnosticLevel.Error, ex);
            throw new IOException("Не удалось подключиться к WebDAV. Проверьте сеть, адрес и сертификат HTTPS.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode; response.Dispose();
            DiagnosticLog.Current.Write(DiagnosticEvent.WebDavRequestFailed, DiagnosticLevel.Error, values: new(Code: code));
            throw new IOException(code switch
            {
                401 or 403 => "WebDAV отказал в доступе. Проверьте логин, пароль приложения и права чтения.",
                >= 300 and < 400 => "WebDAV перенаправляет запрос. Укажите конечный HTTPS-адрес папки.",
                412 => "Файл WebDAV изменился во время копирования. Повторите запуск.",
                _ => $"Запрос WebDAV завершился ошибкой HTTP {code}. Копия не зарегистрирована."
            });
        }
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        { response.Dispose(); throw new InvalidDataException("WebDAV вернул непредусмотренное кодирование содержимого."); }
        return response;
    }
    public async Task<IReadOnlyList<WebDavEntry>> ScanAsync(CancellationToken token = default)
    {
        var entries = new List<WebDavEntry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0; var directoryCount = 0;
        var pending = new Queue<string>(); pending.Enqueue("");
        while (pending.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var uri = Resolve(directory, true);
            using var request = Request(new HttpMethod("PROPFIND"), uri);
            request.Headers.Add("Depth", "1");
            request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
            using var response = await SendAsync(request, token);
            if ((int)response.StatusCode != 207) throw new InvalidDataException("WebDAV должен вернуть список папки (HTTP 207).");
            await using var body = await response.Content.ReadAsStreamAsync(token);
            // Explicit limit also applies to chunked responses. DTD/external entity expansion is prohibited.
            using var buffer = new MemoryStream(); var bytes = new byte[81920]; int read;
            while ((read = await body.ReadAsync(bytes, token)) != 0)
            {
                if (buffer.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("Список папки WebDAV превышает 32 МБ.");
                buffer.Write(bytes, 0, read);
            }
            buffer.Position = 0;
            using var xml = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024, Async = true });
            var document = await XDocument.LoadAsync(xml, LoadOptions.None, token);
            if (document.Root?.Name != Dav + "multistatus") throw new InvalidDataException("Некорректный список WebDAV.");
            var self = false; var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.Root.Elements(Dav + "response"))
            {
                token.ThrowIfCancellationRequested();
                var href = item.Element(Dav + "href")?.Value ?? throw new InvalidDataException("В списке WebDAV отсутствует адрес.");
                var path = RelativeHref(uri, href);
                if (item.Element(Dav + "status") is { } resourceStatus)
                {
                    var fields = resourceStatus.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2 || fields[1] != "200")
                        throw new InvalidDataException("WebDAV не подтвердил доступ к элементу папки.");
                }
                var properties = new XElement(Dav + "prop");
                foreach (var propstat in item.Elements(Dav + "propstat"))
                {
                    var status = propstat.Element(Dav + "status")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (status is { Length: >= 2 } && status[1] == "200")
                        foreach (var prop in propstat.Element(Dav + "prop")?.Elements() ?? []) properties.Add(new XElement(prop));
                }
                if (properties.Elements().GroupBy(prop => prop.Name).Any(group => group.Count() > 1))
                    throw new InvalidDataException("Повторяющиеся свойства WebDAV.");
                var type = properties.Element(Dav + "resourcetype") ?? throw new InvalidDataException("WebDAV не подтвердил тип или доступ к элементу папки.");
                var isDirectory = type.Element(Dav + "collection") is not null;
                if (type.Elements().Any(element => element.Name != Dav + "collection")) throw new InvalidDataException("Неподдерживаемый тип ресурса WebDAV.");
                if (!listed.Add(path)) throw new InvalidDataException("Повторяющийся адрес в списке WebDAV.");
                if (path == directory)
                {
                    if (!isDirectory) throw new InvalidDataException("Источник WebDAV должен быть папкой.");
                    self = true; continue;
                }
                var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                if (parent != directory || !names.Add(path)) throw new InvalidDataException("WebDAV вернул посторонний или повторяющийся путь.");
                long length = 0;
                if (!isDirectory && (!long.TryParse(properties.Element(Dav + "getcontentlength")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0))
                    throw new InvalidDataException("WebDAV не подтвердил размер файла.");
                DateTimeOffset? modified = null;
                if (properties.Element(Dav + "getlastmodified") is { } modifiedProperty)
                {
                    if (!DateTimeOffset.TryParse(modifiedProperty.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
                        throw new InvalidDataException("Некорректная дата изменения WebDAV.");
                    modified = value.ToUniversalTime();
                }
                var etag = properties.Element(Dav + "getetag")?.Value;
                if (etag is not null && (!EntityTagHeaderValue.TryParse(etag, out var parsed) || parsed.Tag == "*" || etag.Length > 1024))
                    throw new InvalidDataException("Некорректный ETag WebDAV.");
                entries.Add(new(path, isDirectory, length, modified, etag));
                if (isDirectory) { pending.Enqueue(path); directoryCount++; } else fileCount++;
                if (directoryCount > 100000 || fileCount > 100000)
                    throw new InvalidDataException("Одна копия WebDAV поддерживает до 100000 файлов и 100000 папок.");
            }
            if (!self) throw new InvalidDataException("WebDAV не подтвердил выбранную папку. Неполный список не принимается.");
        }
        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    }
    private string RelativeHref(Uri requested, string href)
    {
        var raw = href.Split('?', '#')[0];
        if (raw.Split('/').Any(part => Uri.UnescapeDataString(part) is "." or "..")) throw new InvalidDataException("Некорректный адрес WebDAV.");
        if (!Uri.TryCreate(requested, href, out var uri) || uri.Scheme != root.Scheme || uri.Authority != root.Authority
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("WebDAV вернул адрес другого сервера.");
        var rootPath = root.AbsolutePath;
        var escaped = uri.AbsolutePath;
        if (escaped.TrimEnd('/') == rootPath.TrimEnd('/')) return "";
        if (!escaped.StartsWith(rootPath, StringComparison.Ordinal)) throw new InvalidDataException("Адрес WebDAV выходит за выбранную папку.");
        var parts = escaped[rootPath.Length..].TrimEnd('/').Split('/');
        var decoded = parts.Select(Uri.UnescapeDataString).ToArray();
        if (decoded.Any(part => part.Contains('/') || part.Contains('\\'))) throw new InvalidDataException("Некорректное кодирование пути WebDAV.");
        var path = string.Join('/', decoded); FileBackupManifest.ValidateRelativePath(path); return path;
    }
    private Uri Resolve(string path, bool directory)
    {
        if (path.Length == 0) return root;
        FileBackupManifest.ValidateRelativePath(path);
        return new Uri(root, string.Join('/', path.Split('/').Select(Uri.EscapeDataString)) + (directory ? "/" : ""));
    }
    public static bool IsStrongETag(string? etag) => etag is not null && EntityTagHeaderValue.TryParse(etag, out var value) && !value.IsWeak && value.Tag != "*";
    public async Task<string> DownloadAsync(WebDavEntry entry, string path, CancellationToken token = default, IProgress<long>? progress = null)
    {
        if (entry.IsDirectory) throw new ArgumentException("Выберите файл WebDAV.");
        void Report(long bytes)
        {
            if (progress is null) BackupProgressContext.Report(BackupStage.DownloadingFile, bytes, entry.Length);
            else progress.Report(bytes);
        }
        Report(0);
        using var request = Request(HttpMethod.Get, Resolve(entry.Path, false));
        if (IsStrongETag(entry.ETag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(entry.ETag!));
        using var response = await SendAsync(request, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != entry.Length
            || IsStrongETag(entry.ETag) && response.Headers.ETag?.ToString() != entry.ETag)
            throw new IOException("Файл WebDAV изменился или сервер не подтвердил его версию.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > entry.Length) throw new IOException("Размер файла WebDAV изменился во время чтения.");
            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
            Report(total);
        }
        if (total != entry.Length) throw new IOException("Файл WebDAV скачан не полностью.");
        await output.FlushAsync(token); output.Flush(true);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public void Dispose() => http.Dispose();
}
