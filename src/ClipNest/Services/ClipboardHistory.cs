using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipNest.Models;

namespace ClipNest.Services;

public sealed class ClipboardHistory : IDisposable
{
    private const int ThumbnailWidth = 320;
    private const int TextTitleLength = 72;
    private const int TextPreviewLength = 600;
    private const long MaximumImageSnapshotBytes = 256L * 1024 * 1024;
    private static readonly HashSet<string> _imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };
    private readonly string _sessionDirectory;
    private readonly SemaphoreSlim _importGate = new SemaphoreSlim(1, 1);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HashSet<string> _transferredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _isDisposed;
    private int _generation;

    public ClipboardHistory(string? cacheDirectory = null)
    {
        cacheDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipNest", "Sessions");
        Directory.CreateDirectory(cacheDirectory);

        foreach (string directory in Directory.GetDirectories(cacheDirectory))
        {
            TryDeleteDirectory(directory);
        }

        _sessionDirectory = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDirectory);
    }

    public ObservableCollection<ClipboardEntry> Entries { get; } = new ObservableCollection<ClipboardEntry>();
    public int Capacity { get; set; } = Preferences.DefaultCapacity;

    public static bool IsSupportedFile(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    public Task ImportAsync(IReadOnlyList<string> paths, BitmapSource? clipboardImage, CancellationToken cancellationToken)
    {
        return ImportAsync(paths, clipboardImage, null, cancellationToken);
    }

    public async Task ImportAsync(IReadOnlyList<string> paths, BitmapSource? clipboardImage, string? text, CancellationToken cancellationToken)
    {
        int generation = _generation;
        byte[]? imageBytes = clipboardImage == null ? null : CaptureImage(clipboardImage);
        await _importGate.WaitAsync(cancellationToken);

        try
        {
            foreach (string path in paths.TakeLast(Capacity))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ClipboardEntry? entry = await Task.Run(() => CreateFileEntry(path), cancellationToken);
                await _dispatcher.InvokeAsync(() => AddEntry(entry, generation, cancellationToken), DispatcherPriority.Background, cancellationToken);
            }

            if (imageBytes != null)
            {
                ClipboardEntry? entry = await Task.Run(() => CreateImageEntry(imageBytes), cancellationToken);
                await _dispatcher.InvokeAsync(() => AddEntry(entry, generation, cancellationToken), DispatcherPriority.Background, cancellationToken);
            }
            else if (string.IsNullOrWhiteSpace(text) == false)
            {
                ClipboardEntry entry = await Task.Run(() => CreateTextEntry(text), cancellationToken);
                await _dispatcher.InvokeAsync(() => AddEntry(entry, generation, cancellationToken), DispatcherPriority.Background, cancellationToken);
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    public void Trim()
    {
        while (Entries.Count > Capacity)
        {
            DeleteUnsharedFile(Entries[Entries.Count - 1]);
            Entries.RemoveAt(Entries.Count - 1);
        }
    }

    public void Remove(IEnumerable<ClipboardEntry> entries)
    {
        foreach (ClipboardEntry entry in entries.ToArray())
        {
            DeleteUnsharedFile(entry);
            Entries.Remove(entry);
        }
    }

    public void Clear()
    {
        _generation++;
        Remove(Entries.ToArray());
    }

    public void RetainForTransfer(IEnumerable<ClipboardEntry> entries)
    {
        foreach (ClipboardEntry entry in entries.Where(entry => entry.IsOwnedFile))
        {
            _transferredPaths.Add(entry.FilePath);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        Entries.Clear();
        TryDeleteDirectory(_sessionDirectory);
    }

    private void AddEntry(ClipboardEntry? entry, int generation, CancellationToken cancellationToken)
    {
        if (_isDisposed || cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(_sessionDirectory);
            return;
        }

        if (entry == null)
        {
            return;
        }

        if (generation != _generation)
        {
            DeleteUnsharedFile(entry);
            return;
        }

        ClipboardEntry? duplicate = Entries.FirstOrDefault(existing => existing.Fingerprint == entry.Fingerprint);

        if (duplicate != null)
        {
            Entries.Move(Entries.IndexOf(duplicate), 0);
            DeleteUnsharedFile(entry);
            return;
        }

        Entries.Insert(0, entry);
        Trim();
    }

    private ClipboardEntry? CreateFileEntry(string originalPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(originalPath);

            if (Directory.Exists(fullPath))
            {
                DirectoryInfo directory = new DirectoryInfo(fullPath);
                return new ClipboardEntry { FilePath = fullPath, Fingerprint = "folder:" + fullPath.ToUpperInvariant(), Title = directory.Name, Details = "ПАПКА", Preview = fullPath, Glyph = "▱", Kind = ClipboardEntryKind.Folder };
            }

            FileInfo file = new FileInfo(fullPath);

            if (file.Exists == false)
            {
                return null;
            }

            string extension = file.Extension.ToLowerInvariant();
            bool isImage = _imageExtensions.Contains(extension);
            BitmapSource? thumbnail = isImage ? TryCreateThumbnail(fullPath) : null;
            bool hasSnapshot = thumbnail != null && file.Length <= MaximumImageSnapshotBytes;
            string storedPath = fullPath;
            string fingerprint = $"file:{fullPath.ToUpperInvariant()}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";

            if (hasSnapshot)
            {
                string itemDirectory = Path.Combine(_sessionDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(itemDirectory);
                storedPath = Path.Combine(itemDirectory, file.Name);
                File.Copy(fullPath, storedPath);
                using FileStream input = File.OpenRead(storedPath);
                fingerprint = "image:" + Convert.ToHexString(SHA256.HashData(input));
            }

            string type = extension.Length == 0 ? "ФАЙЛ" : extension.TrimStart('.').ToUpperInvariant();
            string size = FormatSize(file.Length);
            string details = $"{type} · {size}";
            return new ClipboardEntry { FilePath = storedPath, Fingerprint = fingerprint, Thumbnail = thumbnail, Title = file.Name, Details = details, Preview = fullPath, Glyph = "▤", Kind = thumbnail != null ? ClipboardEntryKind.Image : ClipboardEntryKind.File, IsOwnedFile = hasSnapshot };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] CaptureImage(BitmapSource image)
    {
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using MemoryStream stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private ClipboardEntry? CreateImageEntry(byte[] imageBytes)
    {
        string fileName = "Clipboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".png";
        string destination = Path.Combine(_sessionDirectory, fileName);

        try
        {
            File.WriteAllBytes(destination, imageBytes);
            using FileStream input = File.OpenRead(destination);
            string fingerprint = "image:" + Convert.ToHexString(SHA256.HashData(input));
            input.Position = 0;
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource image = decoder.Frames[0];
            string details = $"{image.PixelWidth} × {image.PixelHeight} · PNG";
            return new ClipboardEntry { FilePath = destination, Fingerprint = fingerprint, Thumbnail = TryCreateThumbnail(destination), Title = "Изображение", Details = details, Kind = ClipboardEntryKind.Image, IsOwnedFile = true, Glyph = "▧" };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            TryDeleteFile(destination);
            return null;
        }
    }

    private static ClipboardEntry CreateTextEntry(string text)
    {
        string fingerprint = "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        string firstLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        string title = firstLine.Length > TextTitleLength ? firstLine.Substring(0, TextTitleLength) + "…" : firstLine;
        string preview = text.Length > TextPreviewLength ? text.Substring(0, TextPreviewLength) + "…" : text;
        bool isLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        string details = isLink ? "ССЫЛКА" : $"ТЕКСТ · {text.Length} символов";
        return new ClipboardEntry { Fingerprint = fingerprint, Text = text, Title = title, Preview = preview, Details = details, Kind = ClipboardEntryKind.Text, Glyph = isLink ? "↗" : "Aa" };
    }

    private static BitmapSource? TryCreateThumbnail(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            BitmapImage thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.StreamSource = stream;
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelWidth = ThumbnailWidth;
            thumbnail.EndInit();
            thumbnail.Freeze();
            return thumbnail;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        const double BytesPerKilobyte = 1024;
        const double BytesPerMegabyte = BytesPerKilobyte * BytesPerKilobyte;
        const double BytesPerGigabyte = BytesPerMegabyte * BytesPerKilobyte;

        if (bytes >= BytesPerGigabyte)
        {
            double size = bytes / BytesPerGigabyte;
            return $"{size:0.##} ГБ";
        }

        if (bytes >= BytesPerMegabyte)
        {
            double size = bytes / BytesPerMegabyte;
            return $"{size:0.##} МБ";
        }

        double kilobytes = bytes / BytesPerKilobyte;
        return $"{kilobytes:0.#} КБ";
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DeleteUnsharedFile(ClipboardEntry entry)
    {
        if (entry.IsOwnedFile && _transferredPaths.Contains(entry.FilePath) == false)
        {
            TryDeleteFile(entry.FilePath);
        }
    }
}
