using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TiffLightViewer;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;
    private readonly DataClasysFileService _fileService;
    private readonly string? _initialPath;
    private readonly List<BitmapSource> _pages = [];
    private readonly List<string> _folderTiffs = [];
    private PreparedTiffFile? _currentFile;
    private int _pageIndex;
    private int _rotation;
    private double _zoom = 1.0;
    private double _fitZoom = 1.0;
    private bool _fitToWindow = true;
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

            var prepared = await _fileService.PrepareAsync(path);
            var loadedPages = LoadTiffPages(prepared.DisplayPath);
            if (loadedPages.Count == 0)
            {
                throw new InvalidOperationException("表示できるページがありません。");
            }

            _currentFile = prepared;
            _pages.Clear();
            _pages.AddRange(loadedPages);
            _pageIndex = 0;
            _rotation = 0;
            _fitToWindow = true;
            ImageRotateTransform.Angle = 0;
            RefreshFolderList(prepared.OriginalPath);
            ShowCurrentPage();

            Title = $"{Path.GetFileName(prepared.OriginalPath)} - Tiff Light Viewer";
            TitleText.Text = prepared.OriginalPath;
            EmptyText.Visibility = Visibility.Collapsed;
            SetStatus($"{prepared.Mode}: {prepared.OriginalPath}");
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

    private static List<BitmapSource> LoadTiffPages(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var pages = new List<BitmapSource>(decoder.Frames.Count);
        foreach (var frame in decoder.Frames)
        {
            frame.Freeze();
            pages.Add(frame);
        }

        return pages;
    }

    private void ShowCurrentPage()
    {
        if (_pages.Count == 0)
        {
            ViewerImage.Source = null;
            EmptyText.Visibility = Visibility.Visible;
            PageText.Text = "-";
            ZoomText.Text = "-";
            return;
        }

        ViewerImage.Source = _pages[_pageIndex];
        PageText.Text = $"{_pageIndex + 1} / {_pages.Count}";
        ApplyZoom();
        UpdatePanCursor();
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

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        ChangePage(-1);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        ChangePage(1);
    }

    private void ChangePage(int delta)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        var next = _pageIndex + delta;
        if (next < 0 || next >= _pages.Count)
        {
            return;
        }

        _pageIndex = next;
        ShowCurrentPage();
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
        ImageScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - delta.X);
        ImageScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - delta.Y);
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
        RefreshFitZoom();
        if (_fitToWindow)
        {
            ApplyZoom();
            ResetScrollToCenter();
        }
        else if (_zoom < ClampZoom(_zoom))
        {
            _zoom = ClampZoom(_zoom);
            ApplyZoom();
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
                ChangePage(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                ChangePage(1);
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
        _fitToWindow = false;
        _zoom = ClampZoom(value);
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

        _fitToWindow = false;
        _zoom = ClampZoom(newZoom);
        ApplyZoom();
        ImageScrollViewer.UpdateLayout();

        ImageScrollViewer.ScrollToHorizontalOffset((normalizedX * _zoom) - viewportPoint.X);
        ImageScrollViewer.ScrollToVerticalOffset((normalizedY * _zoom) - viewportPoint.Y);
    }

    private void ApplyZoom()
    {
        if (ViewerImage.Source is BitmapSource source && _fitToWindow)
        {
            _fitZoom = CalculateFitZoom(source);
            _zoom = _fitZoom;
        }
        else if (ViewerImage.Source is BitmapSource fitSource)
        {
            _fitZoom = CalculateFitZoom(fitSource);
            _zoom = ClampZoom(_zoom);
        }

        UpdateImageHostSize();
        ImageScrollViewer.UpdateLayout();
        ZoomText.Text = $"{_zoom:P0}";
        UpdatePanCursor();
    }

    private void RefreshFitZoom()
    {
        if (ViewerImage.Source is BitmapSource source)
        {
            _fitZoom = CalculateFitZoom(source);
        }
    }

    private double ClampZoom(double value)
    {
        var minimumZoom = Math.Max(0.05, Math.Min(_fitZoom, 1.0));
        return Math.Clamp(value, minimumZoom, 8.0);
    }

    private void UpdateImageHostSize()
    {
        if (ViewerImage.Source is not BitmapSource source)
        {
            ImageHost.Width = double.NaN;
            ImageHost.Height = double.NaN;
            return;
        }

        var width = source.Width;
        var height = source.Height;
        ViewerImage.Width = source.Width * _zoom;
        ViewerImage.Height = source.Height * _zoom;
        if (_rotation is 90 or 270)
        {
            (width, height) = (height, width);
        }

        var viewportWidth = Math.Max(1, ImageScrollViewer.ViewportWidth);
        var viewportHeight = Math.Max(1, ImageScrollViewer.ViewportHeight);
        ImageHost.Width = Math.Max(viewportWidth, (width * _zoom) + 48);
        ImageHost.Height = Math.Max(viewportHeight, (height * _zoom) + 48);
    }

    private double CalculateFitZoom(BitmapSource source)
    {
        var viewportWidth = Math.Max(1, ImageScrollViewer.ViewportWidth - 24);
        var viewportHeight = Math.Max(1, ImageScrollViewer.ViewportHeight - 24);
        var width = source.Width;
        var height = source.Height;
        if (_rotation is 90 or 270)
        {
            (width, height) = (height, width);
        }

        if (width <= 0 || height <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(Math.Min(viewportWidth / width, viewportHeight / height), 0.05, 1.0);
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
        ImageScrollViewer.ScrollToHorizontalOffset(ImageScrollViewer.ScrollableWidth / 2);
        ImageScrollViewer.ScrollToVerticalOffset(ImageScrollViewer.ScrollableHeight / 2);
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
