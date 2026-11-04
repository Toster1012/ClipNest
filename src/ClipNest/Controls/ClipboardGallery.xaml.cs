using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Controls;

public partial class ClipboardGallery : UserControl
{
    public event Action? DragStarted;
    public event Action<DragDropEffects>? DragFinished;
    public event Action? Copied;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(ClipboardGallery), new PropertyMetadata(false, OnLayoutChanged));
    private const int SelectionScrollMilliseconds = 70;
    private readonly DispatcherTimer _selectionScroll;
    private ClipboardHistory? _history;
    private ICollectionView? _entriesView;
    private Point _dragOrigin;
    private ClipboardEntry? _pressedEntry;
    private ClipboardEntry[] _selectionBeforeGesture = Array.Empty<ClipboardEntry>();
    private bool _isPointerPending;
    private bool _wasSelected;
    private bool _isRangeSelection;
    private bool _isDragHandle;
    private bool _hasMoved;
    private int _rangeEndIndex;

    public ClipboardGallery()
    {
        InitializeComponent();
        _selectionScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SelectionScrollMilliseconds) };
        _selectionScroll.Tick += OnSelectionScroll;
        IsVisibleChanged += (sender, eventArgs) =>
        {
            if (IsVisible == false)
            {
                CancelPointerGesture();
            }
        };
        ApplyLayout();
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool IsMenuOpen => EntriesList.ContextMenu?.IsOpen == true;

    public void Bind(ClipboardHistory history)
    {
        _history = history;
        _entriesView = new ListCollectionView(history.Entries);
        _entriesView.Filter = IsMatchingSearch;
        EntriesList.ItemsSource = _entriesView;
        history.Entries.CollectionChanged += (sender, eventArgs) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        bool isEmpty = EntriesList.Items.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = string.IsNullOrEmpty(SearchInput.Text) ? "Буфер пока пуст" : "Ничего не найдено";
    }

    private void CancelPointerGesture()
    {
        _isPointerPending = false;
        _selectionScroll.Stop();

        if (EntriesList.IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        ((ClipboardGallery)sender).ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (EntriesList == null)
        {
            return;
        }

        EntriesList.ItemTemplate = (DataTemplate)Resources[IsCompact ? "CompactCard" : "LargeCard"];
        FrameworkElementFactory panel = new FrameworkElementFactory(IsCompact ? typeof(VirtualizingStackPanel) : typeof(WrapPanel));
        EntriesList.ItemsPanel = new ItemsPanelTemplate(panel);
    }

    private bool IsMatchingSearch(object value)
    {
        ClipboardEntry entry = (ClipboardEntry)value;
        string query = SearchInput.Text.Trim();
        return query.Length == 0 || entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Preview.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Details.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs eventArgs)
    {
        _entriesView?.Refresh();

        if (EmptyState != null)
        {
            Refresh();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        Refresh();
    }

    private void OnSelectAll(object sender, RoutedEventArgs eventArgs)
    {
        EntriesList.SelectAll();
    }

    private void OnRemove(object sender, RoutedEventArgs eventArgs)
    {
        _history?.Remove(EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray());
    }

    private void OnClear(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Clipboard.Clear();
            _history?.Clear();
            SearchInput.Clear();
            Status.Visibility = Visibility.Collapsed;
        }
        catch (COMException)
        {
            SetStatus("Буфер занят другим приложением. Попробуйте ещё раз.");
        }
    }

    private void OnCopy(object sender, RoutedEventArgs eventArgs)
    {
        CopySelection();
    }

    private void OnCopyEntry(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement element && element.DataContext is ClipboardEntry entry && EntriesList.SelectedItems.Contains(entry) == false)
        {
            EntriesList.UnselectAll();
            EntriesList.SelectedItems.Add(entry);
        }

        CopySelection();
        eventArgs.Handled = true;
    }

    private void CopySelection()
    {
        ClipboardEntry[] entries = EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray();

        if (entries.Length == 0 || _history == null)
        {
            return;
        }

        try
        {
            DataObject data = ClipboardTransfer.CreateData(entries, _history);
            Clipboard.SetDataObject(data, true);
            Status.Visibility = Visibility.Collapsed;
            Copied?.Invoke();
        }
        catch (Exception exception) when (exception is COMException or IOException or NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
        {
            SetStatus(exception.Message);
        }
    }

    private void OnContextSelection(object sender, MouseButtonEventArgs eventArgs)
    {
        ClipboardEntry? entry = FindEntry(eventArgs.OriginalSource as DependencyObject);

        if (entry != null && EntriesList.SelectedItems.Contains(entry) == false)
        {
            EntriesList.UnselectAll();
            EntriesList.SelectedItems.Add(entry);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        DependencyObject? source = eventArgs.OriginalSource as DependencyObject;

        if (IsCopyButton(source))
        {
            return;
        }

        _pressedEntry = FindEntry(source);

        if (_pressedEntry == null || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        _selectionBeforeGesture = EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray();
        _wasSelected = EntriesList.SelectedItems.Contains(_pressedEntry);
        _isDragHandle = HasDragHandle(source);
        _isPointerPending = true;
        _isRangeSelection = false;
        _hasMoved = false;
        _dragOrigin = eventArgs.GetPosition(EntriesList);
        EntriesList.Focus();

        if (_wasSelected == false)
        {
            EntriesList.SelectedItems.Add(_pressedEntry);
        }

        Mouse.Capture(EntriesList, CaptureMode.SubTree);
        eventArgs.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_isPointerPending == false)
        {
            return;
        }

        if (_isPointerPending && _hasMoved == false && _wasSelected && _pressedEntry != null && _isDragHandle == false)
        {
            EntriesList.SelectedItems.Remove(_pressedEntry);
        }

        _isPointerPending = false;
        _selectionScroll.Stop();

        if (EntriesList.IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }

        eventArgs.Handled = true;
    }

    private void OnLostCapture(object sender, MouseEventArgs eventArgs)
    {
        _isPointerPending = false;
        _selectionScroll.Stop();
    }

    private void OnMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (_isPointerPending == false || eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point point = eventArgs.GetPosition(EntriesList);

        if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _hasMoved = true;
        bool isInside = point.X >= 0 && point.Y >= 0 && point.X < EntriesList.ActualWidth && point.Y < EntriesList.ActualHeight;

        if (_isDragHandle == false && isInside)
        {
            _selectionScroll.Stop();
            ClipboardEntry? hovered = FindEntry(EntriesList.InputHitTest(point) as DependencyObject);

            if (hovered != null && hovered != _pressedEntry)
            {
                _isRangeSelection = true;
                SelectRange(hovered);
            }

            return;
        }

        if (_isRangeSelection && _isDragHandle == false)
        {
            if (point.Y < 0 || point.Y >= EntriesList.ActualHeight)
            {
                _selectionScroll.Start();
            }
            else
            {
                _selectionScroll.Stop();
            }

            return;
        }

        StartDrag();
    }

    private void SelectRange(ClipboardEntry hovered)
    {
        int anchor = EntriesList.Items.IndexOf(_pressedEntry);
        int end = EntriesList.Items.IndexOf(hovered);
        _rangeEndIndex = end;

        if (anchor < 0 || end < 0)
        {
            return;
        }

        int first = Math.Min(anchor, end);
        int last = Math.Max(anchor, end);
        HashSet<ClipboardEntry> selected = new HashSet<ClipboardEntry>(_selectionBeforeGesture);

        for (int index = first; index <= last; index++)
        {
            selected.Add((ClipboardEntry)EntriesList.Items[index]);
        }

        foreach (ClipboardEntry entry in EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray())
        {
            if (selected.Contains(entry) == false)
            {
                EntriesList.SelectedItems.Remove(entry);
            }
        }

        foreach (ClipboardEntry entry in selected)
        {
            if (EntriesList.SelectedItems.Contains(entry) == false)
            {
                EntriesList.SelectedItems.Add(entry);
            }
        }
    }

    private void StartDrag()
    {
        _isPointerPending = false;
        _selectionScroll.Stop();
        ClipboardEntry[] entries = EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray();
        Mouse.Capture(null);

        if (entries.Length == 0 || _history == null)
        {
            return;
        }

        DragDropEffects result = DragDropEffects.None;

        try
        {
            DataObject data = ClipboardTransfer.CreateData(entries, _history);
            DragStarted?.Invoke();
            result = DragDrop.DoDragDrop(EntriesList, data, DragDropEffects.Copy);
        }
        catch (Exception exception) when (exception is COMException or IOException or NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
        {
            SetStatus(exception.Message);
        }
        finally
        {
            DragFinished?.Invoke(result);
        }
    }

    private ClipboardEntry? FindEntry(DependencyObject? element)
    {
        return element == null ? null : (ItemsControl.ContainerFromElement(EntriesList, element) as ListBoxItem)?.DataContext as ClipboardEntry;
    }

    private void OnSelectionScroll(object? sender, EventArgs eventArgs)
    {
        if (_isPointerPending == false || Mouse.LeftButton != MouseButtonState.Pressed || EntriesList.Items.Count == 0)
        {
            _selectionScroll.Stop();
            return;
        }

        Point point = Mouse.GetPosition(EntriesList);
        int direction = point.Y < 0 ? -1 : 1;
        int nextIndex = Math.Clamp(_rangeEndIndex + direction, 0, EntriesList.Items.Count - 1);
        ClipboardEntry entry = (ClipboardEntry)EntriesList.Items[nextIndex];
        EntriesList.ScrollIntoView(entry);
        SelectRange(entry);
    }

    private static bool IsCopyButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = GetParent(element);
        }

        return false;
    }

    private static bool HasDragHandle(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is FrameworkElement framework && framework.Tag is string tag && tag == "DragHandle")
            {
                return true;
            }

            element = GetParent(element);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        return element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
    }

    private void OnKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Delete)
        {
            _history?.Remove(EntriesList.SelectedItems.Cast<ClipboardEntry>().ToArray());
        }
        else if (eventArgs.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            EntriesList.SelectAll();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelection();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Enter)
        {
            CopySelection();
            eventArgs.Handled = true;
        }
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        Status.Visibility = Visibility.Visible;
    }
}
