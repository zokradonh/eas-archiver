using Avalonia.Controls;
using Avalonia.Input;

namespace EasArchiver.Gui;

public partial class ConfirmContinueDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmContinueDialog() => InitializeComponent();

    public ConfirmContinueDialog(int requestCount) : this()
    {
        MessageBlock.Text = $"{requestCount} requests have been sent. Continue syncing?";
    }

    private void OnContinue(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnStop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter)  { Result = true;  Close(); }
        else if (e.Key == Key.Escape) { Result = false; Close(); }
    }
}
