using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AFKNotifier.Services;

public sealed record UpdateInfo(Version Version, string TagName, string AssetName, string DownloadUrl);

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MerrickBro/AFK-Notifier/releases/latest";

    private readonly HttpClient _httpClient;
    private string? _stagedDirectory;

    private UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AFK-Notifier");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public static UpdateService Instance { get; } = new();

    public Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    public string CurrentVersionDisplay => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    public bool HasStagedUpdate => !string.IsNullOrWhiteSpace(_stagedDirectory) && Directory.Exists(_stagedDirectory);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("No public GitHub release exists yet.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }

        var tagName = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName) || !TryParseVersion(tagName, out var latestVersion))
        {
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");
        }

        if (latestVersion <= CurrentVersion)
        {
            return null;
        }

        var assetName = GetAssetName();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var downloadUrl = asset.GetProperty("browser_download_url").GetString();
            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                return new UpdateInfo(latestVersion, tagName, assetName, downloadUrl);
            }
        }

        throw new InvalidDataException($"Release {tagName} does not contain {assetName}.");
    }

    public async Task StageUpdateAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        var updateRoot = Path.Combine(Path.GetTempPath(), "AFKNotifier", "updates", update.Version.ToString());
        var archivePath = Path.Combine(updateRoot, update.AssetName);
        var extractPath = Path.Combine(updateRoot, "payload");

        if (Directory.Exists(updateRoot))
        {
            Directory.Delete(updateRoot, true);
        }

        Directory.CreateDirectory(updateRoot);

        using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(archivePath);
            await source.CopyToAsync(destination, cancellationToken);
        }

        if (new FileInfo(archivePath).Length < 1024)
        {
            throw new InvalidDataException("The downloaded update archive is unexpectedly small.");
        }

        ZipFile.ExtractToDirectory(archivePath, extractPath, true);

        var appExecutable = Path.Combine(extractPath, "AFKNotifier.exe");
        var updaterExecutable = Path.Combine(extractPath, "AFKNotifier.Updater.exe");
        var recognizerExecutable = Path.Combine(extractPath, "RecognizerWorker", "AFKNotifier.RecognizerWorker.exe");
        var fontFile = Path.Combine(extractPath, "Fonts", "3270-Regular.ttf");

        if (!File.Exists(appExecutable) ||
            !File.Exists(updaterExecutable) ||
            !File.Exists(recognizerExecutable) ||
            !File.Exists(fontFile))
        {
            throw new InvalidDataException("The downloaded update package is incomplete.");
        }

        if (new FileInfo(fontFile).Length < 10000)
        {
            throw new InvalidDataException("The downloaded update package contains an invalid font file.");
        }

        _stagedDirectory = extractPath;
    }

    public void TryLaunchPendingUpdate()
    {
        if (!HasStagedUpdate)
        {
            return;
        }

        try
        {
            var installedUpdater = Path.Combine(AppContext.BaseDirectory, "AFKNotifier.Updater.exe");
            if (!File.Exists(installedUpdater))
            {
                return;
            }

            var tempUpdater = Path.Combine(Path.GetTempPath(), $"AFKNotifier.Updater-{Guid.NewGuid():N}.exe");
            File.Copy(installedUpdater, tempUpdater, true);

            var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var executablePath = Path.Combine(installDirectory, "AFKNotifier.exe");

            var startInfo = new ProcessStartInfo(tempUpdater)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(_stagedDirectory!);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("--exe");
            startInfo.ArgumentList.Add(executablePath);

            Process.Start(startInfo);
        }
        catch
        {
        }
    }

    private static string GetAssetName()
    {
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        return $"AFK-Notifier-{runtime}.zip";
    }

    private static bool TryParseVersion(string tagName, out Version version)
    {
        var normalized = tagName.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        if (Version.TryParse(normalized, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }
}
