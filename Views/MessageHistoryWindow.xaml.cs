using System.Windows;

namespace YouTubeTool.Views;

public partial class MessageHistoryWindow : Window
{
    public MessageHistoryWindow(IEnumerable<string> messages)
    {
        InitializeComponent();
        MessageList.ItemsSource = messages.ToList();
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = MessageList.SelectedItems.Cast<string>().ToList();
        if (selected.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, selected));
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.ItemsSource is IEnumerable<string> items)
            Clipboard.SetText(string.Join(Environment.NewLine, items));
    }
}
