using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YouTubeTool.ViewModels;

namespace YouTubeTool;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private ChannelItem? _draggedChannel;
    private ChannelListItem? _draggedList;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            Title = $"YouTubeTool v{version.Major}.{version.Minor}.{version.Build}";
        Loaded += async (_, _) => await viewModel.InitializeAsync();
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
