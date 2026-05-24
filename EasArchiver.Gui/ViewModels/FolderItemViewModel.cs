using CommunityToolkit.Mvvm.ComponentModel;

namespace EasArchiver.Gui.ViewModels;

public partial class FolderItemViewModel : ObservableObject
{
    [ObservableProperty] private bool isSelected = true;

    public string Path { get; }
    public string DisplayName { get; }
    public string ParentPath { get; }
    public int Depth { get; }
    public double IndentMargin => Depth * 16.0;

    public FolderItemViewModel(string path)
    {
        Path = path;
        var segments = path.Split('/');
        Depth = segments.Length - 1;
        DisplayName = segments[^1];
        ParentPath = Depth > 0
            ? string.Join(" / ", segments[..^1]) + " /"
            : "";
    }
}
