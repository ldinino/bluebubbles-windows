using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class SyncPage : Page
{
    private readonly SetupViewModel _vm;

    public SyncPage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        InitializeComponent();

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SetupViewModel.StatusMessage):
                    DispatcherQueue.TryEnqueue(() =>
                        SyncStatusText.Text = _vm.StatusMessage);
                    break;
                case nameof(SetupViewModel.SyncProgressValue):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SyncProgressBar.IsIndeterminate = _vm.SyncProgressValue <= 0;
                        SyncProgressBar.Value = _vm.SyncProgressValue;
                    });
                    break;
                case nameof(SetupViewModel.ErrorMessage):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SyncErrorBar.Message = _vm.ErrorMessage ?? string.Empty;
                        SyncErrorBar.IsOpen = _vm.ErrorMessage is not null;
                    });
                    break;
            }
        };

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsSyncing)
            await _vm.RunSyncCommand.ExecuteAsync(null);
    }
}
