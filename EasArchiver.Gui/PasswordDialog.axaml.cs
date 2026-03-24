using Avalonia.Controls;
using Avalonia.Input;

namespace EasArchiver.Gui;

public partial class PasswordDialog : Window
{
    public string? Result { get; private set; }

    public PasswordDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PasswordBox.Focus();
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = PasswordBox.Text;
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnOk(sender, e);
        else if (e.Key == Key.Escape)
            OnCancel(sender, e);
    }
}
