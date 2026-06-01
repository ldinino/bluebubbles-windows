using BlueBubbles.Windows.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace BlueBubbles.Windows.Views.Setup;

public sealed partial class GoogleSignInPage : Page
{
    private readonly SetupViewModel _vm;
    private const string CallbackPrefix = "http://localhost:8641/oauth/callback";

    public GoogleSignInPage()
    {
        _vm = App.Services.GetRequiredService<SetupViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => OAuthWebView.Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await OAuthWebView.EnsureCoreWebView2Async();
            OAuthWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            OAuthWebView.Source = new Uri(_vm.GoogleOAuthUrl);
            LoadingRing.IsActive = false;
        }
        catch
        {
            LoadingRing.IsActive = false;
            _vm.ErrorMessage = "WebView2 is not available. Use manual connection instead.";
            _vm.CurrentStep = SetupStep.ServerConnect;
        }
    }

    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!args.Uri.StartsWith(CallbackPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        args.Cancel = true;
        var token = ExtractAccessToken(args.Uri);

        if (token is not null)
        {
            _vm.OnGoogleTokenReceived(token);
        }
        else
        {
            _vm.ErrorMessage = "Failed to obtain access token from Google.";
            _vm.CurrentStep = SetupStep.ServerConnect;
        }
    }

    private static string? ExtractAccessToken(string url)
    {
        var fragmentIndex = url.IndexOf('#');
        if (fragmentIndex < 0) return null;

        var fragment = url[(fragmentIndex + 1)..];
        var pairs = fragment.Split('&');

        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0] == "access_token")
                return Uri.UnescapeDataString(parts[1]);
        }

        return null;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
        => _vm.CurrentStep = SetupStep.ServerConnect;
}
