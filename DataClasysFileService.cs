using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace TiffLightViewer;

public sealed class DataClasysFileService : IDisposable
{
    private const string DefaultClsuClientPath = @"C:\Program Files\DataClasys\UserClient\CLSUClient.exe";
    private static readonly TimeSpan DecryptReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DecryptReadyPollInterval = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<string, PreparedTiffFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionRoot;
    private bool _disposed;

    public DataClasysFileService()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _sessionRoot = Path.Combine(root, "TiffLightViewer", "Workspaces", DateTime.Now.ToString("yyyyMMdd_HHmmss_ffff"));
        Directory.CreateDirectory(_sessionRoot);
    }

    public async Task<PreparedTiffFile> PrepareAsync(string originalPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(originalPath))
        {
            throw new ArgumentException("ファイルパスが空です。", nameof(originalPath));
        }

        var fullPath = Path.GetFullPath(originalPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("ファイルが見つかりません。", fullPath);
        }

        if (_cache.TryGetValue(fullPath, out var cached) && File.Exists(cached.DisplayPath))
        {
            return cached;
        }

        if (CanReadAsTiff(fullPath))
        {
            var plain = new PreparedTiffFile(fullPath, fullPath, false, "平文TIFF");
            _cache[fullPath] = plain;
            return plain;
        }

        if (!File.Exists(DefaultClsuClientPath))
        {
            throw new InvalidOperationException(
                "TIFFヘッダを確認できず、DataClasysクライアントも見つかりませんでした。");
        }

        var workDir = Path.Combine(_sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var workPath = Path.Combine(workDir, Path.GetFileName(fullPath));
        File.Copy(fullPath, workPath, true);

        await DecryptAsync(workPath, cancellationToken).ConfigureAwait(false);
        await WaitUntilPlainTiffReadyAsync(workPath, cancellationToken).ConfigureAwait(false);

        var decrypted = new PreparedTiffFile(fullPath, workPath, true, "DataClasys復号済み");
        _cache[fullPath] = decrypted;
        return decrypted;
    }

    public static bool IsTiffExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikePlainTiff(string path)
    {
        Span<byte> header = stackalloc byte[4];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Read(header) < header.Length)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return header is [0x49, 0x49, 0x2A, 0x00]
            || header is [0x4D, 0x4D, 0x00, 0x2A]
            || header is [0x49, 0x49, 0x2B, 0x00]
            || header is [0x4D, 0x4D, 0x00, 0x2B];
    }

    private static bool CanReadAsTiff(string path)
    {
        if (LooksLikePlainTiff(path))
        {
            return true;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames.Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (FileFormatException)
        {
            return false;
        }
    }

    private static async Task DecryptAsync(string targetPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DefaultClsuClientPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("DataClasys復号プロセスを開始できませんでした。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("DataClasys復号がタイムアウトしました。");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"DataClasys復号に失敗しました。終了コード: {process.ExitCode} {details}");
        }
    }

    private static async Task WaitUntilPlainTiffReadyAsync(string path, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        long? lastSize = null;
        string? lastSignature = null;
        var stableCount = 0;

        while (started.Elapsed < DecryptReadyTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists)
                {
                    var currentSize = fileInfo.Length;
                    var currentSignature = ReadQuickSignature(path);
                    stableCount = currentSize == lastSize && currentSignature == lastSignature
                        ? stableCount + 1
                        : 0;

                    if (stableCount >= 1 && CanReadAsTiff(path))
                    {
                        return;
                    }

                    lastSize = currentSize;
                    lastSignature = currentSignature;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(DecryptReadyPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("DataClasys復号後、TIFFとして読める状態になるまで待機しましたがタイムアウトしました。");
    }

    private static string ReadQuickSignature(string path)
    {
        Span<byte> buffer = stackalloc byte[64 * 1024];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = stream.Read(buffer);
        return Convert.ToHexString(buffer[..read]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Clear();

        try
        {
            if (Directory.Exists(_sessionRoot))
            {
                Directory.Delete(_sessionRoot, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed record PreparedTiffFile(string OriginalPath, string DisplayPath, bool IsDecrypted, string Mode);
