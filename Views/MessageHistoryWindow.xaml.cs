using System.Windows;
using System.Windows.Media;

namespace YouTubeTool.Views;

public partial class MessageHistoryWindow : Window
{
    public MessageHistoryWindow(IEnumerable<string> messages, double uiScale = 1.0)
    {
        InitializeComponent();
        MessageList.ItemsSource = messages.ToList();
        if (uiScale != 1.0)
            RootPanel.LayoutTransform = new ScaleTransform(uiScale, uiScale);
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
