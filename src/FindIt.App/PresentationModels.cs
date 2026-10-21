using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Media.Imaging;
using FindIt.Shared;
using Microsoft.Win32.SafeHandles;

namespace FindIt.App;

public sealed class ResultTile : INotifyPropertyChanged
{
    public required SearchResult Result { get; init; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Changed(); Changed(nameof(AccessibleName)); } }
    private double _tileWidth = 210;
    public double TileWidth { get => _tileWidth; set { if (Math.Abs(value - _tileWidth) < .001) return; _tileWidth = value; Changed(); } }
    private double _thumbnailHeight = 148;
    public double ThumbnailHeight { get => _thumbnailHeight; set { if (Math.Abs(value - _thumbnailHeight) < .001) return; _thumbnailHeight = value; Changed(); } }
    private BitmapSource? _thumbnail;
    private double? _decodedAspectRatio;
    public BitmapSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; if (value is { PixelWidth: > 0, PixelHeight: > 0 }) _decodedAspectRatio = value.PixelWidth / (double)value.PixelHeight; Changed(); Changed(nameof(HasTextPreview)); Changed(nameof(GlyphVisibility)); } }
    public double AspectRatio => _decodedAspectRatio ?? (Result.Thumbnail is { Width: > 0, Height: > 0 } image ? image.Width / (double)image.Height : Result.Kind switch { FileKind.Video => 16d / 9, FileKind.Document => .78, FileKind.Audio => 1, _ => 4d / 3 });
    public string ExtensionLabel => Path.GetExtension(Result.Name).TrimStart('.').ToUpperInvariant();
    public bool IsDocument => Result.Kind == FileKind.Document;
    public bool IsTimedMedia => Result.Kind is FileKind.Video or FileKind.Audio;
    public bool IsPossibleMatch => Result.MatchCategory == MatchCategory.PossiblePersonAction;
    public string MediaBadge => (Result.Moments.FirstOrDefault()?.PersonEvidence.FirstOrDefault()?.StartMs ?? Result.Moments.FirstOrDefault()?.StartMs) is long time ? $"▶ {TimeSpan.FromMilliseconds(time):hh\\:mm\\:ss}" : Result.Kind == FileKind.Audio ? "♪ Audio" : "▶ Video";
    public string KindLabel => Result.Kind switch { FileKind.Image => "Photo", FileKind.Video => "Video", FileKind.Document => "Document", FileKind.Audio => "Audio", _ => "File" };
    public FindItIconKind IconKind => Result.Kind switch { FileKind.Image => FindItIconKind.Photo, FileKind.Video => FindItIconKind.Video, FileKind.Audio => FindItIconKind.Audio, _ => FindItIconKind.Document };
    public string KindGlyph => Result.Kind switch { FileKind.Image => "\uEB9F", FileKind.Video => "\uE714", FileKind.Audio => "\uE8D6", _ => "\uE8A5" };
    public bool HasTextPreview => Thumbnail is null && Result.Kind == FileKind.Document && !string.IsNullOrWhiteSpace(Result.Snippet);
    public Visibility GlyphVisibility => Thumbnail is not null || HasTextPreview ? Visibility.Collapsed : Visibility.Visible;
    public string DateLabel => Result.ModifiedUtc.ToLocalTime().ToString("dd MMM yyyy");
    public string AccessibleName => $"{(IsSelected ? "Selected, " : "")}{Result.Name}, {KindLabel}, {Result.RelativePath}. Open preview.";
    public string Subtitle => Result.MatchCategory == MatchCategory.PossiblePersonAction ? "Possible person/action match" : (Result.Moments.FirstOrDefault()?.PersonEvidence.FirstOrDefault()?.StartMs ?? Result.Moments.FirstOrDefault()?.StartMs) is long time ? $"{TimeSpan.FromMilliseconds(time):hh\\:mm\\:ss} · {Result.Moments.Count} matching {(Result.Moments.Count == 1 ? "moment" : "moments")}" : $"{KindLabel} · {DateLabel}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record ResultRow(IReadOnlyList<ResultTile> Tiles, double Extent);

public sealed record EvidenceFaceTile(string Name, string Detail, BitmapSource? Portrait)
{
    public string AccessibleName => $"{Name}. {Detail}. Sampled face evidence.";
}
public sealed record FolderChoice(string Label, string? Path);
public sealed class TextPresenceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class FolderCaptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string path ? path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? path : "Folder";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = System.Convert.ToInt64(value, culture);
        return $"{count:N0} {parameter}{(count == 1 ? "" : "s")}";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

internal sealed class ThumbnailCache
{
    private readonly string _root;
    private readonly SemaphoreSlim _decodeSlots = new(2);
    private readonly Dictionary<string, BitmapSource> _cache = [];
    private readonly Queue<string> _order = [];
    private long _bytes;
    private const long Budget = 64 * 1024 * 1024;
    public ThumbnailCache(string root) => _root = Path.GetFullPath(root);
    public async Task<BitmapSource?> ReadAsync(BlobReference? reference, CancellationToken token)
    {
        if (reference is null || reference.Offset < 0 || reference.Length <= 0 || reference.Length > 32 * 1024 * 1024) return null;
        var key = $"{reference.RelativePath}|{reference.Offset}|{reference.Length}";
        lock (_cache) if (_cache.TryGetValue(key, out var existing)) return existing;
        await _decodeSlots.WaitAsync(token);
        try
        {
            return await Task.Run(async () =>
            {
                var path = ResolveArchiveBlob(reference.RelativePath);
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
                ValidateHandleLocation(stream.SafeFileHandle);
                if (reference.Offset > stream.Length - reference.Length) return null;
                stream.Position = reference.Offset;
                var data = new byte[reference.Length];
                await stream.ReadExactlyAsync(data, token);
                using var imageStream = new MemoryStream(data, false);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = Math.Min(440, reference.Width > 0 ? reference.Width : 440); image.StreamSource = imageStream; image.EndInit(); image.Freeze();
                lock (_cache)
                {
                    if (!_cache.ContainsKey(key)) { _cache[key] = image; _order.Enqueue(key); _bytes += (long)image.PixelWidth * image.PixelHeight * 4; }
                    while (_bytes > Budget && _order.TryDequeue(out var oldest)) if (_cache.Remove(oldest, out var old)) _bytes -= (long)old.PixelWidth * old.PixelHeight * 4;
                }
                return image;
            }, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or FormatException or ArgumentException) { return null; }
        finally { _decodeSlots.Release(); }
    }

    private string ResolveArchiveBlob(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0')) throw new IOException("Invalid thumbnail path.");
        var components = relative.Replace('\\', '/').Split('/');
        if (components.Length < 3 || !components[0].Equals("FindIt", StringComparison.OrdinalIgnoreCase) || !components[1].Equals("archive", StringComparison.OrdinalIgnoreCase) || components.Any(part => string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))) throw new IOException("Thumbnail data must be inside this drive's archive.");
        var path = Path.GetFullPath(Path.Combine(_root, Path.Combine(components)));
        if (!IsArchivePath(path)) throw new IOException("Thumbnail path leaves the archive.");
        for (var current = path; ; current = Path.GetDirectoryName(current) ?? throw new IOException("Invalid thumbnail path."))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked thumbnail paths are not supported.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(_root), StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }
    private bool IsArchivePath(string path) => path.StartsWith(Path.Combine(_root, "FindIt", "archive") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void ValidateHandleLocation(SafeFileHandle handle)
    {
        // Validate the opened handle as well, closing the check/open race for redirected parents.
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Could not validate thumbnail location.");
        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        if (!IsArchivePath(final)) throw new IOException("Thumbnail handle leaves the archive.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint characters, uint flags);
}

public sealed class PersonTile : INotifyPropertyChanged
{
    public double ThumbnailHeight => 132;
    public required PersonRecord Person { get; init; }
    private double _tileWidth = 160;
    public double TileWidth { get => _tileWidth; set { if (Math.Abs(value - _tileWidth) < .001) return; _tileWidth = value; Changed(); } }
    private BitmapSource? _portrait;
    public BitmapSource? Portrait { get => _portrait; set { _portrait = value; Changed(); } }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Changed(); Changed(nameof(AccessibleName)); } }
    public string Initials => string.Concat(Person.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(name => StringInfo.GetNextTextElement(name))).ToUpperInvariant();
    public string AccessibleName => $"{(IsSelected ? "Selected, " : "")}{Person.Name}, {Person.ObservationCount:N0} observation{(Person.ObservationCount == 1 ? "" : "s")}. Open person details.";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed record PersonRow(IReadOnlyList<PersonTile> Tiles);
