using System.Windows;

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
        Clipboard.SetText(ErrorText.Text);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
