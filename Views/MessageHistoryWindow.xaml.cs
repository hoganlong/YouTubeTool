using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
            CopyWithRetry(string.Join(Environment.NewLine, selected), sender as Button);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageList.ItemsSource is IEnumerable<string> items)
            CopyWithRetry(string.Join(Environment.NewLine, items), sender as Button);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Clipboard.SetText can throw COMException (CLIPBRD_E_CANT_OPEN) when another
    // process is holding the clipboard. Retry with SetDataObject, which is more robust.
    private static void CopyWithRetry(string text, Button? button)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                if (button != null) button.Content = "Copied!";
                return;
            }
            catch (COMException) when (attempt < 9)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (COMException)
            {
                if (button != null) button.Content = "Copy failed";
            }
        }
    }
}
