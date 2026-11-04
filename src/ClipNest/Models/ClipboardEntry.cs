using System;
using System.Windows.Media.Imaging;

namespace ClipNest.Models;

public sealed class ClipboardEntry
{
    public string FilePath { get; init; } = string.Empty;
    public required string Fingerprint { get; init; }
    public BitmapSource? Thumbnail { get; init; }
    public required string Title { get; init; }
    public required string Details { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Preview { get; init; } = string.Empty;
    public string Glyph { get; init; } = "▤";
    public ClipboardEntryKind Kind { get; init; }
    public bool IsOwnedFile { get; init; }
    public bool HasThumbnail => Thumbnail != null;
    public bool IsText => Kind == ClipboardEntryKind.Text;
    public DateTime CapturedAt { get; init; } = DateTime.Now;
}
