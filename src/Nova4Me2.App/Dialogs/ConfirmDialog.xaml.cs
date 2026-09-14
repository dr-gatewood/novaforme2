using System.Windows;

namespace Nova4Me2.App.Dialogs;

public partial class ConfirmDialog : Window
{
    private readonly string _word;

    public ConfirmDialog(string title, string message, string word)
    {
        InitializeComponent();
        _word = word;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Prompt.Text = $"Type {word} to confirm:";
        Input.TextChanged += (_, _) => OkButton.IsEnabled = Input.Text.Trim() == word;
        Loaded += (_, _) => Input.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { if (Input.Text.Trim() == _word) DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
