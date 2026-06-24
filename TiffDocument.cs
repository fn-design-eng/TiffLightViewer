using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace TiffLightViewer;

public sealed class TiffDocument : IDisposable
{
    private const int CacheRadius = 1;
    private const int MaxPreviewPixels = 16_000_000;
    private readonly Dictionary<int, TiffPage> _cache = [];
    private readonly object _cacheLock = new();
    private readonly TiffDecoderBackend _backend;
    private readonly string _path;
    private bool _disposed;

    public TiffDocument(string path)
    {
        _path = path;
        try
        {
            PageCount = ReadWpfPageCount(path);
            _backend = TiffDecoderBackend.Wpf;
        }
        catch (Exception) when (DataClasysFileService.LooksLikePlainTiff(path))
        {
            PageCount = ReadMagickPageCount(path);
            _backend = TiffDecoderBackend.Magick;
        }
    }

    public int PageCount { get; }

    public Task<TiffPage> LoadPageAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _backend == TiffDecoderBackend.Magick
                ? LoadMagickPage(pageIndex)
                : LoadWpfPage(pageIndex);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                _cache[pageIndex] = page;
                TrimCache(pageIndex);
            }

            _ = Task.Run(() => PreloadNeighbors(pageIndex), CancellationToken.None);
            return page;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    private static int ReadWpfPageCount(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return decoder.Frames.Count;
    }

    private static int ReadMagickPageCount(string path)
    {
        using var images = new MagickImageCollection(path);
        return images.Count;
    }

    private TiffPage LoadWpfPage(int pageIndex)
    {
        using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[pageIndex];
        var originalWidth = frame.PixelWidth;
        var originalHeight = frame.PixelHeight;
        var source = DownscaleIfHuge(frame);
        source.Freeze();
        return new TiffPage(source, originalWidth, originalHeight);
    }

    private TiffPage LoadMagickPage(int pageIndex)
    {
        using var images = new MagickImageCollection(_path);
        using var image = images[pageIndex];
        var originalWidth = (int)Math.Min(int.MaxValue, image.Width);
        var originalHeight = (int)Math.Min(int.MaxValue, image.Height);
        DownscaleIfHuge(image);

        var bytes = image.ToByteArray(MagickFormat.Png);
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        return new TiffPage(bitmap, originalWidth, originalHeight);
    }

    private static BitmapSource DownscaleIfHuge(BitmapSource source)
    {
        var pixelCount = source.PixelWidth * (long)source.PixelHeight;
        if (pixelCount <= MaxPreviewPixels)
        {
            return source;
        }

        var scale = Math.Sqrt(MaxPreviewPixels / (double)pixelCount);
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static void DownscaleIfHuge(IMagickImage<byte> image)
    {
        var pixelCount = image.Width * (long)image.Height;
        if (pixelCount <= MaxPreviewPixels)
        {
            return;
        }

        var scale = Math.Sqrt(MaxPreviewPixels / (double)pixelCount);
        var width = Math.Max(1, (uint)Math.Round(image.Width * scale));
        var height = Math.Max(1, (uint)Math.Round(image.Height * scale));
        image.Resize(width, height);
    }

    private void PreloadNeighbors(int centerPageIndex)
    {
        for (var pageIndex = centerPageIndex - CacheRadius; pageIndex <= centerPageIndex + CacheRadius; pageIndex++)
        {
            if (pageIndex < 0 || pageIndex >= PageCount || pageIndex == centerPageIndex)
            {
                continue;
            }

            lock (_cacheLock)
            {
                if (_cache.ContainsKey(pageIndex))
                {
                    continue;
                }
            }

            try
            {
                var page = _backend == TiffDecoderBackend.Magick
                    ? LoadMagickPage(pageIndex)
                    : LoadWpfPage(pageIndex);
                lock (_cacheLock)
                {
                    _cache.TryAdd(pageIndex, page);
                    TrimCache(centerPageIndex);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void TrimCache(int centerPageIndex)
    {
        var staleKeys = _cache.Keys
            .Where(key => Math.Abs(key - centerPageIndex) > CacheRadius)
            .ToArray();

        foreach (var key in staleKeys)
        {
            _cache.Remove(key);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal enum TiffDecoderBackend
{
    Wpf,
    Magick,
}
