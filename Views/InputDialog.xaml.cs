using System.Windows;
using System.Windows.Input;

namespace YouTubeTool.Views;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = string.Empty;

    public InputDialog(string prompt, string title = "Input")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Result = InputBox.Text; DialogResult = true; }
    }
}
