using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using ClipNest.Models;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipNest.Services;
using Forms = System.Windows.Forms;

namespace ClipNest;

public partial class ClipboardPopup : Window
{
    private const int ForegroundPollMilliseconds = 100;
    private readonly DispatcherTimer _foregroundPoll;
    private bool _isExiting;
    private bool _isDragging;
    private readonly ClipboardHistory _history;
    private readonly Preferences _preferences;
    private readonly double _preferredWidth;
    private readonly double _preferredHeight;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private PasteTarget _pasteTarget;
    private bool _isPasting;

    public bool IsPasting => _isPasting;

    public ClipboardPopup(ClipboardHistory history, Preferences preferences)
    {
        InitializeComponent();
        _history = history;
        _preferences = preferences;
        _preferredWidth = Width;
        _preferredHeight = Height;
        Gallery.Bind(history);
        Gallery.PasteRequested += OnPasteRequested;
        Gallery.DragStarted += OnDragStarted;
        Gallery.DragFinished += OnDragFinished;
        Gallery.Copied += Hide;
        _foregroundPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ForegroundPollMilliseconds) };
        _foregroundPoll.Tick += OnForegroundPoll;
        IsVisibleChanged += (sender, eventArgs) =>
        {
            if (IsVisible)
            {
                _foregroundPoll.Start();
            }
            else
            {
                _foregroundPoll.Stop();
            }
        };
    }

    public void ShowPanel()
    {
        if (_isPasting || IsVisible)
        {
            return;
        }

        _pasteTarget = WindowFocus.CaptureTarget();
        Gallery.ResetSelection();
        Show();
        PopupPlacement.Position(this, _preferences.OpenPopupInScreenCorner, _preferredWidth, _preferredHeight);
        WindowFocus.Activate(this);
    }

    public void Refresh()
    {
        Gallery.Refresh();
    }

    public void HandlePointerPressed(int screenX, int screenY)
    {
        if (IsVisible == false || _isDragging)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;

        if (WindowFocus.IsInsideWindow(handle, screenX, screenY))
        {
            return;
        }

        if (Gallery.IsMenuOpen && WindowFocus.IsApplicationWindowAtPoint(screenX, screenY))
        {
            return;
        }

        Hide();
    }

    public void PrepareShutdown()
    {
        _isExiting = true;
        _shutdown.Cancel();
        _foregroundPoll.Stop();
        Close();
    }

    private async void OnPasteRequested(ClipboardEntry[] entries)
    {
        if (_isPasting || entries.Length == 0)
        {
            return;
        }

        if (WindowFocus.IsTargetValid(_pasteTarget) == false)
        {
            Gallery.ShowStatus("Сначала выберите поле в другом приложении и откройте Win+V.");
            return;
        }

        _isPasting = true;

        try
        {
            DataObject[] items = entries.Select(entry => ClipboardTransfer.CreateData(new[] { entry }, _history)).ToArray();
            await ClipboardPaste.PasteAsync(_pasteTarget, items, Hide, _shutdown.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_isExiting == false)
            {
                MessageBox.Show(exception.Message, "ClipNest — вставка остановлена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            _isPasting = false;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        WindowAppearance.Apply(this, false);
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_isExiting == false)
        {
            eventArgs.Cancel = true;
            Hide();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            Hide();
            eventArgs.Handled = true;
        }
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            if (_isDragging == false && IsActive == false && Gallery.IsMenuOpen == false)
            {
                Hide();
            }
        }));
    }

    private void OnDragStarted()
    {
        _isDragging = true;
    }

    private void OnForegroundPoll(object? sender, EventArgs eventArgs)
    {
        if (_isDragging == false && Gallery.IsMenuOpen == false && WindowFocus.IsForegroundWindow(new WindowInteropHelper(this).Handle) == false)
        {
            Hide();
        }
    }

    private void OnDragFinished(DragDropEffects result)
    {
        _isDragging = false;

        if (result != DragDropEffects.None)
        {
            Hide();
        }
        else
        {
            WindowFocus.Activate(this);
        }
    }
}
