using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using YouTubeTool.ViewModels;

namespace YouTubeTool;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private ChannelItem? _draggedChannel;
    private ChannelListItem? _draggedList;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            Title = $"YouTubeTool v{version.Major}.{version.Minor}.{version.Build}";
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        SourceInitialized += MainWindow_SourceInitialized;
    }

    // WPF sets Window.Icon via the pack URI, but the taskbar sometimes shows a blank or stale
    // icon — usually after a rebuild or when Windows' icon cache hands back the wrong entry.
    // Explicitly extracting both icon sizes from the exe (where ApplicationIcon embeds them)
    // and pushing them into the HWND via WM_SETICON forces the shell to use them right away.
    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exePath = Environment.ProcessPath;
            if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(exePath)) return;

            var large = new IntPtr[1];
            var small = new IntPtr[1];
            if (ExtractIconEx(exePath, 0, large, small, 1) > 0)
            {
                if (small[0] != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, small[0]);
                if (large[0] != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, large[0]);
            }
        }
        catch { }
    }

    private void ChannelListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggedChannel = null;
    }

    private void ChannelListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item == null) return;
        _draggedChannel = item.DataContext as ChannelItem;
        if (_draggedChannel == null) return;

        DragDrop.DoDragDrop(ChannelListBox, _draggedChannel, DragDropEffects.Move);
        _draggedChannel = null; // clear if drag was cancelled without a drop
    }

    private void ChannelListBox_Drop(object sender, DragEventArgs e)
    {
        if (_draggedChannel == null) return;
        var dragged = _draggedChannel;
        _draggedChannel = null;

        var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as ChannelItem;
        if (target == null || target == dragged) return;

        ((MainViewModel)DataContext).MoveChannel(dragged, target);
    }

    private void ListsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void ListsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (item == null) return;
        _draggedList = item.DataContext as ChannelListItem;
        if (_draggedList == null) return;

        DragDrop.DoDragDrop(ListsListBox, _draggedList, DragDropEffects.Move);
        _draggedList = null;
    }

    private void ListsListBox_Drop(object sender, DragEventArgs e)
    {
        if (_draggedList != null)
        {
            var dragged = _draggedList;
            _draggedList = null;
            var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as ChannelListItem;
            if (target == null || target == dragged) return;
            ((MainViewModel)DataContext).MoveList(dragged, target);
        }
        else if (_draggedChannel != null)
        {
            var dragged = _draggedChannel;
            _draggedChannel = null;
            var targetList = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as ChannelListItem;
            if (targetList == null) return;
            _ = ((MainViewModel)DataContext).MoveChannelToListAsync(dragged, targetList);
        }
    }

    private void RefreshAllDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void RefreshMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string tag && DataContext is MainViewModel vm)
            vm.SetRefreshMode(tag);
    }

    private static T? FindAncestor<T>(DependencyObject obj) where T : DependencyObject
    {
        while (obj != null)
        {
            if (obj is T result) return result;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
}
