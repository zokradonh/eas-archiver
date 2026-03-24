using Avalonia.Controls;
using EasArchiver.Gui.ViewModels;

namespace EasArchiver.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
        {
            vm.RequestPassword = async () =>
            {
                var dialog = new PasswordDialog();
                await dialog.ShowDialog(this);
                return dialog.Result;
            };
        }
    }
}
