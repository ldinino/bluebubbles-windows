using CommunityToolkit.Mvvm.ComponentModel;

namespace BlueBubbles.Core.Configuration;

public partial class ServerConfiguration : ObservableObject
{
    [ObservableProperty] public partial string Password { get; set; }
    [ObservableProperty] public partial string ServerUrl { get; set; }
    [ObservableProperty] public partial string ProxyService { get; set; }
    [ObservableProperty] public partial Dictionary<string, string> CustomHeaders { get; set; }
    [ObservableProperty] public partial string? LocalhostPort { get; set; }

    // FCM data for Firebase URL re-resolution
    [ObservableProperty] public partial string? FcmProjectId { get; set; }
    [ObservableProperty] public partial string? FcmStorageBucket { get; set; }
    [ObservableProperty] public partial string? FcmApiKey { get; set; }
    [ObservableProperty] public partial string? FcmFirebaseUrl { get; set; }
    [ObservableProperty] public partial string? FcmClientId { get; set; }
    [ObservableProperty] public partial string? FcmApplicationId { get; set; }

    public bool HasValidFcmData =>
        FcmProjectId is not null && FcmApiKey is not null && FcmApplicationId is not null;

    public ServerConfiguration()
    {
        Password = string.Empty;
        ServerUrl = string.Empty;
        ProxyService = string.Empty;
        CustomHeaders = new Dictionary<string, string>();
    }
}
