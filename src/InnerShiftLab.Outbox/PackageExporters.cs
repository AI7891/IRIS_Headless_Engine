// =============================================================================
//  Package exporters — get the built package to where the operator's phone can
//  reach it. Google Drive is the primary target (service-account key, no user
//  OAuth tokens at rest); the local exporter is the fallback when Drive isn't
//  configured, leaving the package in output/outbox/ for manual pickup.
// =============================================================================
using Google.Apis.Auth.OAuth2;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InnerShiftLab.Outbox;

public interface IPackageExporter
{
    /// <summary>Exports the package and returns a reference the operator can open (Drive folder URL or local path).</summary>
    Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default);
}

/// <summary>No-op exporter: the package already lives under output/outbox/.</summary>
public sealed class LocalPackageExporter : IPackageExporter
{
    private readonly ILogger<LocalPackageExporter> _log;
    public LocalPackageExporter(ILogger<LocalPackageExporter> log) => _log = log;

    public Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default)
    {
        _log.LogInformation("Google Drive export not configured; package stays at {Dir}", package.PackageDir);
        return Task.FromResult(package.PackageDir);
    }
}

/// <summary>
/// Uploads the package directory tree to a Google Drive folder using the raw
/// Drive v3 REST API (resumable uploads, so videos work too). Auth is a
/// service-account key — nothing user-scoped is stored anywhere.
/// </summary>
public sealed class GoogleDrivePackageExporter : IPackageExporter
{
    private const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";

    private readonly IHttpClientFactory _httpFactory;
    private readonly OutboxSettings _settings;
    private readonly ILogger<GoogleDrivePackageExporter> _log;

    public GoogleDrivePackageExporter(IHttpClientFactory httpFactory, OutboxSettings settings,
        ILogger<GoogleDrivePackageExporter> log)
    {
        _httpFactory = httpFactory; _settings = settings; _log = log;
    }

    public async Task<string> ExportAsync(OutboxPackage package, CancellationToken ct = default)
    {
        var drive = _settings.GoogleDrive;
        var keyPath = string.IsNullOrWhiteSpace(drive.ServiceAccountJsonPath)
            ? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
            : drive.ServiceAccountJsonPath;
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException(
                "Google Drive export is enabled but no service-account key was found. " +
                "Set Outbox:GoogleDrive:ServiceAccountJsonPath or the GOOGLE_APPLICATION_CREDENTIALS env var.");
        if (string.IsNullOrWhiteSpace(drive.FolderId))
            throw new InvalidOperationException(
                "Google Drive export is enabled but Outbox:GoogleDrive:FolderId is empty. " +
                "Share a Drive folder with the service-account email and put its id here.");

        var credential = GoogleCredential.FromFile(keyPath).CreateScoped(DriveScope);
        var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);

        using var client = _httpFactory.CreateClient("gdrive");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var packageFolderId = await CreateFolderAsync(client,
            $"{package.CreatedAt:yyyy-MM-dd} {package.HookId}", drive.FolderId, ct);
        await UploadDirectoryAsync(client, package.PackageDir, packageFolderId, ct);

        var url = $"https://drive.google.com/drive/folders/{packageFolderId}";
        _log.LogInformation("Outbox package {PackageId} exported to Google Drive: {Url}", package.PackageId, url);
        return url;
    }

    /// <summary>Mirrors a local directory tree (any depth) into a Drive folder.</summary>
    private async Task UploadDirectoryAsync(HttpClient client, string dir, string parentId, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            await UploadFileAsync(client, file, parentId, ct);
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var subFolderId = await CreateFolderAsync(client, Path.GetFileName(sub), parentId, ct);
            await UploadDirectoryAsync(client, sub, subFolderId, ct);
        }
    }

    private static async Task<string> CreateFolderAsync(HttpClient client, string name, string parentId, CancellationToken ct)
    {
        var body = JsonConvert.SerializeObject(new
        {
            name,
            mimeType = "application/vnd.google-apps.folder",
            parents = new[] { parentId },
        });
        using var resp = await client.PostAsync($"{FilesEndpoint}?supportsAllDrives=true",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive folder create failed ({(int)resp.StatusCode}): {json}");
        return JObject.Parse(json).Value<string>("id")
            ?? throw new InvalidOperationException("Drive folder create returned no id");
    }

    private async Task UploadFileAsync(HttpClient client, string path, string parentId, CancellationToken ct)
    {
        // Resumable upload: one request to open the session, one PUT with the bytes.
        var metadata = JsonConvert.SerializeObject(new { name = Path.GetFileName(path), parents = new[] { parentId } });
        using var start = new HttpRequestMessage(HttpMethod.Post, $"{UploadEndpoint}?uploadType=resumable&supportsAllDrives=true")
        {
            Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
        };
        using var startResp = await client.SendAsync(start, ct);
        if (!startResp.IsSuccessStatusCode || startResp.Headers.Location == null)
            throw new InvalidOperationException(
                $"Drive upload session failed for {Path.GetFileName(path)} ({(int)startResp.StatusCode}): {await startResp.Content.ReadAsStringAsync(ct)}");

        await using var stream = File.OpenRead(path);
        using var put = new HttpRequestMessage(HttpMethod.Put, startResp.Headers.Location)
        {
            Content = new StreamContent(stream),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeFor(path));
        using var putResp = await client.SendAsync(put, ct);
        if (!putResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Drive upload failed for {Path.GetFileName(path)} ({(int)putResp.StatusCode}): {await putResp.Content.ReadAsStringAsync(ct)}");

        _log.LogDebug("Uploaded {File} to Drive folder {Folder}", Path.GetFileName(path), parentId);
    }

    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".json" => "application/json",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
