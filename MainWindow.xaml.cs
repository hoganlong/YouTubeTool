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

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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

    private void ListsListBox_Drop(object sender, DragEventArgs e)
    {
        if (_draggedChannel == null) return;
        var dragged = _draggedChannel;
        _draggedChannel = null;

        var targetList = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as ChannelListItem;
        if (targetList == null) return;

        _ = ((MainViewModel)DataContext).MoveChannelToListAsync(dragged, targetList);
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
