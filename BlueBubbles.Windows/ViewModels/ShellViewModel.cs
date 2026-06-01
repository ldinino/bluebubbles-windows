using BlueBubbles.Core.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueBubbles.Windows.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    [ObservableProperty] public partial bool IsPaneOpen { get; set; }

    public ShellViewModel(AppSettings settings)
    {
        _settings = settings;
        IsPaneOpen = true;
    }
}
