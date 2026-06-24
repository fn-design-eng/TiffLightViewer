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

    public async Task<PreparedTiffFile> PrepareAsync(
        string originalPath,
        bool skipPlainTiffProbe = false,
        Action<string>? progressCallback = null,
        CancellationToken cancellationToken = default)
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

        if (!skipPlainTiffProbe && CanReadAsTiff(fullPath))
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
        progressCallback?.Invoke("DataClasys復号用にローカルコピー中...");
        File.Copy(fullPath, workPath, true);

        progressCallback?.Invoke("DataClasys復号コマンドを実行中...");
        await DecryptAsync(workPath, cancellationToken).ConfigureAwait(false);
        progressCallback?.Invoke("DataClasys復号完了待機中...");
        var displayPath = await WaitUntilPlainTiffReadyAsync(workDir, workPath, progressCallback, cancellationToken)
            .ConfigureAwait(false);

        var decrypted = new PreparedTiffFile(fullPath, displayPath, true, "DataClasys復号済み");
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
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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

    private static async Task<string> WaitUntilPlainTiffReadyAsync(
        string workDir,
        string preferredPath,
        Action<string>? progressCallback,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var lastStates = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        var stableCount = 0;
        var lastProgressSecond = -1;

        while (started.Elapsed < DecryptReadyTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var elapsedSecond = (int)started.Elapsed.TotalSeconds;
                if (elapsedSecond != lastProgressSecond && elapsedSecond % 2 == 0)
                {
                    progressCallback?.Invoke($"DataClasys復号完了待機中... {elapsedSecond}秒");
                    lastProgressSecond = elapsedSecond;
                }

                foreach (var candidatePath in EnumerateDecryptCandidates(workDir, preferredPath))
                {
                    var fileInfo = new FileInfo(candidatePath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    var currentSize = fileInfo.Length;
                    var currentSignature = ReadQuickSignature(candidatePath);
                    var currentState = new FileState(currentSize, currentSignature);
                    stableCount = lastStates.TryGetValue(candidatePath, out var lastState) && currentState == lastState ? 1 : 0;
                    lastStates[candidatePath] = currentState;

                    if (stableCount >= 1 && LooksLikePlainTiff(candidatePath))
                    {
                        return candidatePath;
                    }
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

        var files = EnumerateDecryptCandidates(workDir, preferredPath)
            .Select(path => $"{Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)")
            .ToArray();
        var fileList = files.Length == 0 ? "作業フォルダ内にファイルなし" : string.Join(", ", files);
        throw new TimeoutException($"DataClasys復号後、TIFFヘッダを持つファイルが見つかりませんでした。作業フォルダ: {fileList}");
    }

    private static IEnumerable<string> EnumerateDecryptCandidates(string workDir, string preferredPath)
    {
        if (File.Exists(preferredPath))
        {
            yield return preferredPath;
        }

        foreach (var path in Directory.EnumerateFiles(workDir, "*", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(DataClasysFileService.IsTiffExtension)
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!path.Equals(preferredPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static string ReadQuickSignature(string path)
    {
        Span<byte> buffer = stackalloc byte[64 * 1024];
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(buffer);
        return Convert.ToHexString(buffer[..read]);
    }

    private sealed record FileState(long Size, string Signature);

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
