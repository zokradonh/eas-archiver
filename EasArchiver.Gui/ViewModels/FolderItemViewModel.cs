using CommunityToolkit.Mvvm.ComponentModel;

namespace EasArchiver.Gui.ViewModels;

public partial class FolderItemViewModel : ObservableObject
{
    [ObservableProperty] private bool isSelected = true;

    public string Path { get; }

    public FolderItemViewModel(string path) => Path = path;
}
