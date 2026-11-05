using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipNest.Services;
using Forms = System.Windows.Forms;

namespace ClipNest;

public partial class LibraryWindow : Window
{
    private const int ClipboardRetryLimit = 8;
    private const int ClipboardRetryMilliseconds = 90;
    private const int HotkeyWarningDurationMilliseconds = 4000;
    private readonly Preferences _preferences;
    private readonly ClipboardHistory _history;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly DispatcherTimer _clipboardRetry;
    private readonly DispatcherTimer _updatePoll;
    private readonly ClipboardPopup _popup;
    private ClipboardEvents? _nativeEvents;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;
    private int _retryCount;
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.MinValue;
    private bool _isCheckingUpdates;
    private ReleaseUpdate? _availableUpdate;
    private Version? _offeredUpdateVersion;
    private UpdateWindow? _updateWindow;

    public LibraryWindow()
    {
        InitializeComponent();
        ReleaseUpdate.CleanupCompletedUpdates();
        _preferences = Preferences.Load();
        _history = new ClipboardHistory { Capacity = _preferences.Capacity };
        Gallery.Bind(_history);
        _popup = new ClipboardPopup(_history, _preferences);
        _clipboardRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ClipboardRetryMilliseconds) };
        _clipboardRetry.Tick += OnClipboardRetry;
        _updatePoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _updatePoll.Tick += async (sender, eventArgs) => await CheckForUpdatesAsync(false);
        _updatePoll.Start();
    }

    public void ShowPopup()
    {
        _popup.ShowPanel();
    }

    public void ShutdownResources()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _clipboardRetry.Stop();
        _updatePoll.Stop();
        _updateWindow?.CancelForShutdown();
        _shutdown.Cancel();
        _nativeEvents?.Dispose();
        _trayIcon?.Dispose();
        _popup.PrepareShutdown();
        _history.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        WindowAppearance.Apply(this, true);
        _nativeEvents = new ClipboardEvents(new WindowInteropHelper(this).Handle);
        _nativeEvents.ClipboardChanged += OnClipboardChanged;
        _nativeEvents.HotkeyPressed += TogglePopup;
        _nativeEvents.PointerPressed += _popup.HandlePointerPressed;
        Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Буфер · Win+V", null, (sender, eventArgs) => ShowPopup());
        menu.Items.Add("Открыть коллекцию", null, (sender, eventArgs) => OpenCollection());
        menu.Items.Add("Настройки", null, (sender, eventArgs) => OpenSettings());
        menu.Items.Add("Проверить обновления", null, async (sender, eventArgs) => await CheckForUpdatesAsync(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выйти и очистить историю", null, (sender, eventArgs) => ExitApplication());
        using Stream iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Branding/ClipNest.ico")).Stream;
        _trayIcon = new Forms.NotifyIcon { Text = "ClipNest · Win+V", Icon = new System.Drawing.Icon(iconStream), Visible = true, ContextMenuStrip = menu };
        _trayIcon.DoubleClick += (sender, eventArgs) => ShowPopup();

        if (_nativeEvents.IsHotkeyRegistered == false)
        {
            ShowCaptureError("Перехват Win+V недоступен. Откройте буфер через трей.");
            _trayIcon.ShowBalloonTip(HotkeyWarningDurationMilliseconds, "ClipNest", CaptureStatus.Text, Forms.ToolTipIcon.Warning);
        }
    }

    private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool isManual)
    {
        if (_isCheckingUpdates || _isExiting)
        {
            return;
        }

        if (_updateWindow != null)
        {
            if (isManual)
            {
                _updateWindow.Activate();
            }

            return;
        }

        _isCheckingUpdates = true;

        try
        {
            if (isManual || DateTimeOffset.UtcNow >= _nextUpdateCheck)
            {
                _nextUpdateCheck = DateTimeOffset.UtcNow.AddDays(1);
                _availableUpdate = await ReleaseUpdate.CheckAsync(_shutdown.Token);
            }

            if (_isExiting)
            {
                return;
            }

            if (_availableUpdate != null && (isManual || _offeredUpdateVersion != _availableUpdate.Version) && _popup.IsVisible == false && _popup.IsPasting == false)
            {
                _offeredUpdateVersion = _availableUpdate.Version;
                _updateWindow = new UpdateWindow(_availableUpdate);
                _updateWindow.Closed += (sender, eventArgs) => _updateWindow = null;
                _updateWindow.Show();
            }
            else if (isManual && _availableUpdate == null)
            {
                MessageBox.Show($"Установлена версия {ReleaseUpdate.CurrentVersionText}. Доступных обновлений нет.", "ClipNest");
            }
        }
        catch (OperationCanceledException) when (_isExiting) { }
        catch (Exception exception)
        {
            if (isManual && _isExiting == false)
            {
                MessageBox.Show($"Не удалось проверить обновления: {exception.Message}", "ClipNest");
            }
        }
        finally
        {
            _isCheckingUpdates = false;
        }
    }

    private void OnClipboardChanged()
    {
        _retryCount = 0;
        _clipboardRetry.Stop();
        _clipboardRetry.Start();
    }

    private async void OnClipboardRetry(object? sender, EventArgs eventArgs)
    {
        _clipboardRetry.Stop();

        try
        {
            List<string> paths = new List<string>();
            System.Windows.Media.Imaging.BitmapSource? image = null;
            string? text = null;

            if (Clipboard.ContainsData(ClipboardTransfer.SelectionFormat))
            {
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                StringCollection files = Clipboard.GetFileDropList();
                paths.AddRange(files.Cast<string>());
            }
            else if (Clipboard.ContainsImage() || Clipboard.ContainsData("PNG"))
            {
                image = ClipboardTransfer.ReadImage();
            }
            else if (Clipboard.ContainsText())
            {
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
            }

            await _history.ImportAsync(paths, image, text, _shutdown.Token);
        }
        catch (COMException)
        {
            _retryCount++;

            if (_retryCount < ClipboardRetryLimit && _isExiting == false)
            {
                _clipboardRetry.Start();
            }
            else
            {
                ShowCaptureError("Буфер занят другим приложением. Скопируйте ещё раз.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ShowCaptureError($"Не удалось добавить запись: {exception.Message}");
        }
    }

    private void ShowCaptureError(string message)
    {
        CaptureStatus.Text = message;
        CaptureStatus.Visibility = Visibility.Visible;
    }

    private void TogglePopup()
    {
        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            ShowPopup();
        }
    }

    private void ShowLargeWindow()
    {
        _popup.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenCollection()
    {
        ShowLargeWindow();
        SettingsPanel.Visibility = Visibility.Collapsed;
        Gallery.Visibility = Visibility.Visible;
        CollectionTab.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#3B2E52")!;
        SettingsTab.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void OpenSettings()
    {
        ShowLargeWindow();
        CapacityInput.Text = _preferences.Capacity.ToString();
        StartupInput.IsChecked = Preferences.IsStartupEnabled();
        PopupCornerInput.IsChecked = _preferences.OpenPopupInScreenCorner;
        SettingsPanel.Visibility = Visibility.Visible;
        Gallery.Visibility = Visibility.Collapsed;
        SettingsTab.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#3B2E52")!;
        CollectionTab.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void OnCollection(object sender, RoutedEventArgs eventArgs)
    {
        OpenCollection();
    }

    private void OnSettings(object sender, RoutedEventArgs eventArgs)
    {
        OpenSettings();
    }

    private void OnSaveSettings(object sender, RoutedEventArgs eventArgs)
    {
        if (int.TryParse(CapacityInput.Text, out int capacity) == false || capacity < Preferences.MinimumCapacity || capacity > Preferences.MaximumCapacity)
        {
            SettingsStatus.Text = "Введите целое число от 1 до 1000.";
            return;
        }

        try
        {
            Preferences.SetStartup(StartupInput.IsChecked == true);
            _preferences.Capacity = capacity;
            _preferences.OpenPopupInScreenCorner = PopupCornerInput.IsChecked == true;
            _preferences.Save();
            _history.Capacity = capacity;
            _history.Trim();
            Gallery.Refresh();
            _popup.Refresh();
            SettingsStatus.Text = "Настройки сохранены";
        }
        catch (Exception exception)
        {
            SettingsStatus.Text = exception.Message;
        }
    }

    private void OnHide(object sender, RoutedEventArgs eventArgs)
    {
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_isExiting == false)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    private void ExitApplication()
    {
        ShutdownResources();
        Application.Current.Shutdown();
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Hide();
        }
    }
}
