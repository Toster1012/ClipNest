using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClipNest.Services;

public sealed record ReleaseUpdate(Version Version, Uri DownloadUri, long Size, string Sha256)
{
    public static Version CurrentVersion => typeof(ReleaseUpdate).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentVersionText => CurrentVersion.ToString(3);
    private static readonly HttpClient _client = CreateClient();
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;

    public static void CleanupCompletedUpdates()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipNest", "Updates");

        try
        {
            if (Directory.Exists(root) == false)
            {
                return;
            }

            foreach (string directory in Directory.GetDirectories(root))
            {
                string fullPath = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;

                if (AppContext.BaseDirectory.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase) == false && File.Exists(Path.Combine(directory, "success")))
                {
                    try { Directory.Delete(directory, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static Version? ParseVersion(string tag)
    {
        string value = tag.Trim();

        if (value.StartsWith("release-", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(8);
        }

        if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(1);
        }

        return Version.TryParse(value, out Version? version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision))
            : null;
    }

    public static ReleaseUpdate? ParseRelease(string json, Version currentVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement release = document.RootElement;

        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
        {
            return null;
        }

        Version? version = ParseVersion(release.GetProperty("tag_name").GetString() ?? string.Empty);
        Version current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build), Math.Max(0, currentVersion.Revision));

        if (version == null || version <= current)
        {
            return null;
        }

        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;

            if (name.Equals("win-x64.zip", StringComparison.OrdinalIgnoreCase) == false &&
                (name.StartsWith("ClipNest-", StringComparison.OrdinalIgnoreCase) == false || name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) == false))
            {
                continue;
            }

            string? digest = asset.TryGetProperty("digest", out JsonElement digestValue) ? digestValue.GetString() : null;
            long size = asset.GetProperty("size").GetInt64();
            string download = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;

            if (size <= 0 || size > MaximumArchiveBytes || digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) != true ||
                digest.Length != 71 || digest.Substring(7).Any(character => Uri.IsHexDigit(character) == false) ||
                Uri.TryCreate(download, UriKind.Absolute, out Uri? uri) == false || uri.Scheme != "https" ||
                uri.Host != "github.com" || uri.AbsolutePath.StartsWith("/Toster1012/ClipNest/releases/download/", StringComparison.OrdinalIgnoreCase) == false)
            {
                continue;
            }

            return new ReleaseUpdate(version, uri, size, digest.Substring(7));
        }

        return null;
    }

    public static async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await _client.GetAsync("https://api.github.com/repos/Toster1012/ClipNest/releases/latest", timeout.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token), CurrentVersion);
    }

    public async Task<string> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipNest", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "update.zip");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        try
        {
            using HttpResponseMessage response = await _client.GetAsync(DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (Stream input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (FileStream output = File.Create(archive))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int count;

                while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += count;

                    if (total > Size || total > MaximumArchiveBytes)
                    {
                        throw new InvalidDataException("Размер обновления не совпадает с данными GitHub.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    progress.Report((double)total / Size);
                }

                if (total != Size)
                {
                    throw new InvalidDataException("Архив обновления скачан не полностью.");
                }
            }

            await using (FileStream input = File.OpenRead(archive))
            {
                string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(input, timeout.Token));

                if (actualHash.Equals(Sha256, StringComparison.OrdinalIgnoreCase) == false)
                {
                    throw new InvalidDataException("Контрольная сумма обновления не совпадает. Файлы программы не изменены.");
                }
            }

            string payload = Path.Combine(directory, "payload");
            await Task.Run(() => ExtractArchive(archive, payload), timeout.Token);
            return payload;
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    public static void ExtractArchive(string archive, string destination)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            total = checked(total + entry.Length);

            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) == false || entry.FullName.Contains(':') ||
                names.Add(path) == false || total > MaximumExtractedBytes || names.Count > 10000 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("Недопустимое содержимое архива обновления.");
            }
        }

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(archive, destination);
        string[] executables = Directory.GetFiles(destination, "ClipNest.exe", SearchOption.AllDirectories);

        if (executables.Length != 1)
        {
            throw new InvalidDataException("В архиве не найден единственный ClipNest.exe.");
        }
    }

    public async Task StartInstallerAsync(string payload, CancellationToken cancellationToken)
    {
        string source = Path.GetDirectoryName(Directory.GetFiles(payload, "ClipNest.exe", SearchOption.AllDirectories).Single())!;
        Version binaryVersion = AssemblyName.GetAssemblyName(Path.Combine(source, "ClipNest.dll")).Version ?? new Version(0, 0);

        if (binaryVersion != Version)
        {
            throw new InvalidDataException("Версия приложения в архиве не совпадает с тегом релиза.");
        }

        string destination = AppContext.BaseDirectory;
        string probe = Path.Combine(destination, ".clipnest-update-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
        string work = Path.GetDirectoryName(payload)!;
        string script = Path.Combine(work, "ApplyUpdate.ps1");
        using (Stream input = typeof(ReleaseUpdate).Assembly.GetManifestResourceStream("ClipNest.Services.ApplyUpdate.ps1")!)
        using (StreamReader reader = new StreamReader(input))
        {
            File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true));
        }

        string configuration = Path.Combine(work, "configuration.json");
        string ready = Path.Combine(work, "ready");
        File.WriteAllText(configuration, JsonSerializer.Serialize(new
        {
            ProcessId = Environment.ProcessId,
            ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Source = source,
            Destination = destination,
            Work = work,
            Ready = ready
        }));
        ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", script, "-ConfigurationPath", configuration })
        {
            start.ArgumentList.Add(argument);
        }

        using Process installer = Process.Start(start) ?? throw new IOException("Не удалось запустить установку обновления.");

        try
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(ready))
                {
                    return;
                }

                if (installer.HasExited)
                {
                    break;
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new IOException("Установщик не подтвердил запуск. ClipNest продолжает работать.");
        }
        catch
        {
            if (installer.HasExited == false)
            {
                installer.Kill();
            }

            throw;
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClipNest/" + CurrentVersionText);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
