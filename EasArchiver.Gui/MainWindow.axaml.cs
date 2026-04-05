using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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

            vm.RequestConfirm = async count =>
            {
                var dialog = new ConfirmContinueDialog(count);
                await dialog.ShowDialog(this);
                return dialog.Result;
            };

            vm.BrowseFolder = async currentDir =>
            {
                IStorageFolder? startLocation = null;
                if (!string.IsNullOrWhiteSpace(currentDir))
                {
                    var fullPath = Path.IsPathRooted(currentDir)
                        ? currentDir
                        : Path.GetFullPath(currentDir);
                    if (Directory.Exists(fullPath))
                        startLocation = await StorageProvider.TryGetFolderFromPathAsync(fullPath);
                }
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Archive Directory",
                    AllowMultiple = false,
                    SuggestedStartLocation = startLocation
                });
                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            };
        }
    }
}
