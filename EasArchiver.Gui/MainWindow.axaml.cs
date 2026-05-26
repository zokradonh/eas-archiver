using System.Collections.Specialized;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            // Auto-scroll the log to the newest entry whenever lines are added.
            vm.LogLines.CollectionChanged += OnLogLinesChanged;

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

            vm.ConfirmResetState = async () =>
            {
                var dialog = new ConfirmResetDialog();
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

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var dialog = new SettingsDialog(vm);
            await dialog.ShowDialog(this);
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not MainViewModel vm || vm.LogLines.Count == 0) return;
        // Defer to after layout so the new item exists in the visual tree.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var last = vm.LogLines[^1];
            LogListBox.ScrollIntoView(last);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
