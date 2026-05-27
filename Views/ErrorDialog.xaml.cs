using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace YouTubeTool.Views;

public partial class ErrorDialog : Window
{
    public ErrorDialog(string message)
    {
        InitializeComponent();
        ErrorText.Text = message;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        // Clipboard.SetText can throw COMException (CLIPBRD_E_CANT_OPEN) when another
        // process is holding the clipboard. Retry with SetDataObject, which is more robust.
        var button = sender as Button;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(ErrorText.Text, copy: true);
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

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
