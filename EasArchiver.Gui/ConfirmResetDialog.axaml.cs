using Avalonia.Controls;
using Avalonia.Input;

namespace EasArchiver.Gui;

public partial class ConfirmResetDialog : Window
{
    public bool Result { get; private set; }

    public ConfirmResetDialog() => InitializeComponent();

    private void OnConfirm(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Result = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Result = false; Close(); }
    }
}
