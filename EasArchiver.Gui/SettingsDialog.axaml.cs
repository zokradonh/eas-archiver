using Avalonia.Controls;
using EasArchiver.Gui.ViewModels;

namespace EasArchiver.Gui;

public partial class SettingsDialog : Window
{
    public bool Result { get; private set; }

    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(MainViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SaveConfigCommand.Execute(null);
        Result = true;
        Close();
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Revert in-memory edits by reloading from disk
        if (DataContext is MainViewModel vm)
            vm.LoadConfigCommand.Execute(null);
        Result = false;
        Close();
    }
}
