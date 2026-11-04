using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipNest.Services;
using Forms = System.Windows.Forms;

namespace ClipNest;

public partial class ClipboardPopup : Window
{
    private const int ScreenMargin = 16;
    private const int ForegroundPollMilliseconds = 100;
    private readonly DispatcherTimer _foregroundPoll;
    private bool _isExiting;
    private bool _isDragging;

    public ClipboardPopup(ClipboardHistory history)
    {
        InitializeComponent();
        Gallery.Bind(history);
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
        Show();
        Forms.Screen screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        DpiScale scale = VisualTreeHelper.GetDpi(this);
        Left = (screen.WorkingArea.Right - ScreenMargin) / scale.DpiScaleX - Width;
        Top = (screen.WorkingArea.Bottom - ScreenMargin) / scale.DpiScaleY - Height;
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
        _foregroundPoll.Stop();
        Close();
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
