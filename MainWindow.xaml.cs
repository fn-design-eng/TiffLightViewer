using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TiffLightViewer;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;
    private readonly DataClasysFileService _fileService;
    private readonly string? _initialPath;
    private readonly List<string> _folderTiffs = [];
    private CancellationTokenSource? _pageLoadCts;
    private TiffDocument? _document;
    private TiffPage? _currentPage;
    private PreparedTiffFile? _currentFile;
    private int _pageIndex;
    private int _rotation;
    private double _zoom = 1.0;
    private double _fitZoom = 1.0;
    private bool _fitToWindow = true;
    private bool _fitRefreshQueued;
    private bool _isPanning;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    public MainWindow(DataClasysFileService fileService, string? initialPath)
    {
        _fileService = fileService;
        _initialPath = initialPath;
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplySystemTheme();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySystemTheme();
        if (!string.IsNullOrWhiteSpace(_initialPath))
        {
            await OpenPathAsync(_initialPath);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _document?.Dispose();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyTitleBarTheme();
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            Dispatcher.Invoke(() =>
            {
                ApplySystemTheme();
                ApplyTitleBarTheme();
            });
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "TIFFファイルを開く",
            Filter = "TIFF ファイル (*.tif;*.tiff)|*.tif;*.tiff|すべてのファイル (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenPathAsync(dialog.FileName);
        }
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            SetStatus("読み込み中...");
            IsEnabled = false;

            if (!DataClasysFileService.IsTiffExtension(path))
            {
                throw new InvalidOperationException("TIFFファイルを選択してください。");
            }

            _pageLoadCts?.Cancel();
            _pageLoadCts?.Dispose();
            _pageLoadCts = null;

            var prepared = await _fileService.PrepareAsync(path);
            var document = await Task.Run(() => new TiffDocument(prepared.DisplayPath));
            if (document.PageCount == 0)
            {
                throw new InvalidOperationException("表示できるページがありません。");
            }

            var oldDocument = _document;
            _currentFile = prepared;
            _document = document;
            oldDocument?.Dispose();
            _pageIndex = 0;
            _rotation = 0;
            _fitToWindow = true;
            ImageRotateTransform.Angle = 0;
            RefreshFolderList(prepared.OriginalPath);

            Title = $"{Path.GetFileName(prepared.OriginalPath)} - Tiff Light Viewer";
            TitleText.Text = prepared.OriginalPath;
            EmptyText.Visibility = Visibility.Collapsed;
            SetStatus($"{prepared.Mode}: {prepared.OriginalPath}");
            await ShowCurrentPageAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("読み込みに失敗しました");
        }
        finally
        {
            IsEnabled = true;
            Focus();
        }
    }

    private async Task ShowCurrentPageAsync()
    {
        if (_document is null || _document.PageCount == 0)
        {
            _currentPage = null;
            ViewerImage.Source = null;
            EmptyText.Visibility = Visibility.Visible;
            PageText.Text = "-";
            ZoomText.Text = "-";
            return;
        }

        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = new CancellationTokenSource();
        var token = _pageLoadCts.Token;

        try
        {
            SetStatus($"ページ読み込み中... {_pageIndex + 1} / {_document.PageCount}");
            var page = await _document.LoadPageAsync(_pageIndex, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _currentPage = page;
            ViewerImage.Source = page.Bitmap;
            PageText.Text = $"{_pageIndex + 1} / {_document.PageCount}";
            ApplyZoom();
            UpdatePanCursor();
            SetStatus($"{_currentFile?.Mode}: {_currentFile?.OriginalPath}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RefreshFolderList(string path)
    {
        _folderTiffs.Clear();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _folderTiffs.AddRange(Directory.EnumerateFiles(directory)
            .Where(DataClasysFileService.IsTiffExtension)
            .OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase));
    }

    private async void PreviousFile_Click(object sender, RoutedEventArgs e)
    {
        await OpenSiblingFileAsync(-1);
    }

    private async void NextFile_Click(object sender, RoutedEventArgs e)
    {
        await OpenSiblingFileAsync(1);
    }

    private async Task OpenSiblingFileAsync(int delta)
    {
        if (_currentFile is null || _folderTiffs.Count == 0)
        {
            return;
        }

        var index = _folderTiffs.FindIndex(item =>
            string.Equals(Path.GetFullPath(item), _currentFile.OriginalPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var next = index + delta;
        if (next < 0 || next >= _folderTiffs.Count)
        {
            return;
        }

        await OpenPathAsync(_folderTiffs[next]);
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageAsync(-1);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await ChangePageAsync(1);
    }

    private async Task ChangePageAsync(int delta)
    {
        if (_document is null || _document.PageCount == 0)
        {
            return;
        }

        var next = _pageIndex + delta;
        if (next < 0 || next >= _document.PageCount)
        {
            return;
        }

        _pageIndex = next;
        await ShowCurrentPageAsync();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        SetCustomZoom(ClampZoom(_zoom / 1.25));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        SetCustomZoom(ClampZoom(_zoom * 1.25));
    }

    private void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        SetCustomZoom(1.0);
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _fitToWindow = true;
        ApplyZoom();
        ResetScrollToCenter();
    }

    private void FitToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_fitToWindow)
        {
            SetCustomZoom(1.0);
            return;
        }

        _fitToWindow = true;
        ApplyZoom();
        ResetScrollToCenter();
    }

    private void RotateLeft_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(-90);
    }

    private void RotateRight_Click(object sender, RoutedEventArgs e)
    {
        RotateBy(90);
    }

    private void RotateBy(int degrees)
    {
        _rotation = (_rotation + degrees + 360) % 360;
        ImageRotateTransform.Angle = _rotation;
        if (_fitToWindow)
        {
            ApplyZoom();
            ResetScrollToCenter();
        }
        else
        {
            ApplyZoom();
        }
    }

    private void OpenInPaint_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "mspaint.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(_currentFile.OriginalPath);
            Process.Start(startInfo);
            SetStatus($"ペイントで開きました: {_currentFile.OriginalPath}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ペイント起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mousePosition = e.GetPosition(ImageScrollViewer);
        ZoomAt(mousePosition, e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ImageScrollViewer.UpdateLayout();
        if (!CanPan())
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(ImageScrollViewer);
        _panStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = ImageScrollViewer.VerticalOffset;
        Mouse.Capture(ImageScrollViewer, CaptureMode.SubTree);
        ImageScrollViewer.Cursor = Cursors.SizeAll;
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPanning();
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopPanning();
            return;
        }

        var current = e.GetPosition(ImageScrollViewer);
        var delta = current - _panStart;
        ScrollToClampedOffset(_panStartHorizontalOffset - delta.X, _panStartVerticalOffset - delta.Y);
        e.Handled = true;
    }

    private void ImageScrollViewer_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            UpdatePanCursor();
        }
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow)
        {
            QueueFitToWindowRefresh();
        }
        else if (_zoom < ClampZoom(_zoom))
        {
            _zoom = ClampZoom(_zoom);
            ApplyZoom();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow)
        {
            QueueFitToWindowRefresh();
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                await OpenSiblingFileAsync(-1);
                e.Handled = true;
                break;
            case Key.Right:
                await OpenSiblingFileAsync(1);
                e.Handled = true;
                break;
            case Key.PageUp:
                await ChangePageAsync(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                await ChangePageAsync(1);
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                SetCustomZoom(ClampZoom(_zoom * 1.25));
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                SetCustomZoom(ClampZoom(_zoom / 1.25));
                e.Handled = true;
                break;
            case Key.D1:
            case Key.NumPad1:
                SetCustomZoom(1.0);
                e.Handled = true;
                break;
            case Key.F:
                _fitToWindow = true;
                ApplyZoom();
                ResetScrollToCenter();
                e.Handled = true;
                break;
            case Key.R:
                RotateRight_Click(sender, e);
                e.Handled = true;
                break;
            case Key.L:
                RotateLeft_Click(sender, e);
                e.Handled = true;
                break;
            case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                Open_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void SetCustomZoom(double value)
    {
        _zoom = ClampZoom(value);
        _fitToWindow = IsFitZoom(_zoom);
        ApplyZoom();
    }

    private void ZoomAt(Point viewportPoint, double newZoom)
    {
        if (ViewerImage.Source is null)
        {
            return;
        }

        var oldZoom = _zoom;
        if (oldZoom <= 0)
        {
            oldZoom = 1.0;
        }

        var normalizedX = (ImageScrollViewer.HorizontalOffset + viewportPoint.X) / oldZoom;
        var normalizedY = (ImageScrollViewer.VerticalOffset + viewportPoint.Y) / oldZoom;

        _zoom = ClampZoom(newZoom);
        _fitToWindow = IsFitZoom(_zoom);
        ApplyZoom();
        ImageScrollViewer.UpdateLayout();

        if (_fitToWindow)
        {
            ResetScrollToCenter();
        }
        else
        {
            ScrollToClampedOffset((normalizedX * _zoom) - viewportPoint.X, (normalizedY * _zoom) - viewportPoint.Y);
        }
    }

    private void ApplyZoom()
    {
        if (_currentPage is not null && _fitToWindow)
        {
            _fitZoom = CalculateFitZoom(_currentPage);
            _zoom = _fitZoom;
        }
        else if (_currentPage is not null)
        {
            _fitZoom = CalculateFitZoom(_currentPage);
            _zoom = ClampZoom(_zoom);
        }

        UpdateImageHostSize();
        ImageScrollViewer.UpdateLayout();
        ZoomText.Text = $"{_zoom:P0}";
        UpdatePanCursor();
    }

    private void RefreshFitZoom()
    {
        if (_currentPage is not null)
        {
            _fitZoom = CalculateFitZoom(_currentPage);
        }
    }

    private void QueueFitToWindowRefresh()
    {
        if (_fitRefreshQueued || _currentPage is null)
        {
            return;
        }

        _fitRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitRefreshQueued = false;
            if (!_fitToWindow || _currentPage is null)
            {
                return;
            }

            ApplyZoom();
            ResetScrollToCenter();
        }, DispatcherPriority.Loaded);
    }

    private double ClampZoom(double value)
    {
        var minimumZoom = Math.Max(0.05, Math.Min(_fitZoom, 1.0));
        return Math.Clamp(value, minimumZoom, 8.0);
    }

    private bool IsFitZoom(double zoom)
    {
        return Math.Abs(zoom - _fitZoom) <= 0.0001;
    }

    private void UpdateImageHostSize()
    {
        if (_currentPage is null)
        {
            ImageHost.Width = double.NaN;
            ImageHost.Height = double.NaN;
            return;
        }

        var width = _currentPage.OriginalPixelWidth;
        var height = _currentPage.OriginalPixelHeight;
        ViewerImage.Width = width * _zoom * _currentPage.PreviewScaleX;
        ViewerImage.Height = height * _zoom * _currentPage.PreviewScaleY;
        if (_rotation is 90 or 270)
        {
            (width, height) = (height, width);
        }

        var viewportWidth = Math.Max(1, ImageScrollViewer.ViewportWidth);
        var viewportHeight = Math.Max(1, ImageScrollViewer.ViewportHeight);
        ImageHost.Width = Math.Max(viewportWidth, width * _zoom * _currentPage.PreviewScaleX);
        ImageHost.Height = Math.Max(viewportHeight, height * _zoom * _currentPage.PreviewScaleY);
    }

    private double CalculateFitZoom(TiffPage page)
    {
        var viewportWidth = Math.Max(1, ImageScrollViewer.ViewportWidth - 24);
        var viewportHeight = Math.Max(1, ImageScrollViewer.ViewportHeight - 24);
        var width = page.OriginalPixelWidth;
        var height = page.OriginalPixelHeight;
        var previewScaleX = page.PreviewScaleX;
        var previewScaleY = page.PreviewScaleY;
        if (_rotation is 90 or 270)
        {
            (width, height) = (height, width);
            (previewScaleX, previewScaleY) = (previewScaleY, previewScaleX);
        }

        if (width <= 0 || height <= 0)
        {
            return 1.0;
        }

        var displayedWidthAtOneHundredPercent = width * previewScaleX;
        var displayedHeightAtOneHundredPercent = height * previewScaleY;
        return Math.Clamp(
            Math.Min(viewportWidth / displayedWidthAtOneHundredPercent, viewportHeight / displayedHeightAtOneHundredPercent),
            0.05,
            1.0);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private bool CanPan()
    {
        if (ViewerImage.Source is null)
        {
            return false;
        }

        return _zoom > _fitZoom * 1.01;
    }

    private void UpdatePanCursor()
    {
        if (_isPanning)
        {
            ImageScrollViewer.Cursor = Cursors.SizeAll;
            Cursor = Cursors.SizeAll;
            return;
        }

        var cursor = CanPan() ? Cursors.Hand : Cursors.Arrow;
        ImageScrollViewer.Cursor = cursor;
        Cursor = cursor;
    }

    private void ResetScrollToCenter()
    {
        ImageScrollViewer.UpdateLayout();
        ScrollToClampedOffset(ImageScrollViewer.ScrollableWidth / 2, ImageScrollViewer.ScrollableHeight / 2);
    }

    private void ScrollToClampedOffset(double horizontalOffset, double verticalOffset)
    {
        ImageScrollViewer.ScrollToHorizontalOffset(Math.Clamp(horizontalOffset, 0, ImageScrollViewer.ScrollableWidth));
        ImageScrollViewer.ScrollToVerticalOffset(Math.Clamp(verticalOffset, 0, ImageScrollViewer.ScrollableHeight));
    }

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        if (Mouse.Captured == ImageScrollViewer)
        {
            Mouse.Capture(null);
        }

        UpdatePanCursor();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedTiffPath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var path = GetDroppedTiffPath(e);
        if (path is null)
        {
            SetStatus("TIFFファイルをドロップしてください");
            e.Handled = true;
            return;
        }

        await OpenPathAsync(path);
        e.Handled = true;
    }

    private static string? GetDroppedTiffPath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return null;
        }

        return paths.FirstOrDefault(DataClasysFileService.IsTiffExtension);
    }

    private void ApplySystemTheme()
    {
        var useLightTheme = IsWindowsAppLightTheme();
        ApplyTitleBarTheme(!useLightTheme);

        if (useLightTheme)
        {
            SetBrush("WindowBackgroundBrush", "#EAF1F8");
            SetBrush("ChromeBackgroundBrush", "#DCEAF7");
            SetBrush("ChromeBorderBrush", "#B7CADC");
            SetBrush("ToolbarBackgroundBrush", "#F9FBFD");
            SetBrush("ToolbarButtonBrush", "#F8FBFF");
            SetBrush("ToolbarButtonPrimaryBrush", "#DCEEFF");
            SetBrush("ToolbarButtonBorderBrush", "#8CAFD1");
            SetBrush("TextBrush", "#24394A");
            SetBrush("MutedTextBrush", "#64788A");
            SetBrush("SeparatorBrush", "#CCD8E3");
        }
        else
        {
            SetBrush("WindowBackgroundBrush", "#20252B");
            SetBrush("ChromeBackgroundBrush", "#2B333B");
            SetBrush("ChromeBorderBrush", "#3F4B56");
            SetBrush("ToolbarBackgroundBrush", "#252C33");
            SetBrush("ToolbarButtonBrush", "#303943");
            SetBrush("ToolbarButtonPrimaryBrush", "#304A63");
            SetBrush("ToolbarButtonBorderBrush", "#5D7488");
            SetBrush("TextBrush", "#E8EEF4");
            SetBrush("MutedTextBrush", "#A9B8C5");
            SetBrush("SeparatorBrush", "#53616D");
        }
    }

    private void ApplyTitleBarTheme()
    {
        ApplyTitleBarTheme(!IsWindowsAppLightTheme());
    }

    private void ApplyTitleBarTheme(bool useDarkTitleBar)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = useDarkTitleBar ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20h1, ref value, sizeof(int));
        }
    }

    private void SetBrush(string key, string color)
    {
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static bool IsWindowsAppLightTheme()
    {
        const string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
