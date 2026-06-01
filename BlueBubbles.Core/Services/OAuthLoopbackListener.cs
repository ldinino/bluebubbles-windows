using System.Diagnostics;
using System.Net;
using System.Text;

namespace BlueBubbles.Core.Services;

public static class OAuthLoopbackListener
{
    private const string ListenerPrefix = "http://localhost:8641/";

    private const string FragmentExtractorHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>BlueBubbles</title>
        <style>
        body { font-family: 'Segoe UI', system-ui, sans-serif; display: flex; justify-content: center;
               align-items: center; height: 100vh; margin: 0; background: #f5f5f5; color: #333; }
        .card { text-align: center; padding: 2.5rem; background: white;
                border-radius: 12px; box-shadow: 0 2px 12px rgba(0,0,0,0.08); }
        </style></head>
        <body><div class="card"><p>Completing sign-in&hellip;</p></div>
        <script>
        var h = window.location.hash.substring(1);
        var t = new URLSearchParams(h).get('access_token');
        if (t) window.location.href = '/oauth/complete?access_token=' + encodeURIComponent(t);
        else document.querySelector('.card').innerHTML = '<p>Could not extract token. You may close this tab.</p>';
        </script>
        </body></html>
        """;

    private const string SuccessHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>BlueBubbles</title>
        <style>
        body { font-family: 'Segoe UI', system-ui, sans-serif; display: flex; justify-content: center;
               align-items: center; height: 100vh; margin: 0; background: #f5f5f5; color: #333; }
        .card { text-align: center; padding: 2.5rem; background: white;
                border-radius: 12px; box-shadow: 0 2px 12px rgba(0,0,0,0.08); }
        .check { font-size: 3rem; }
        </style></head>
        <body><div class="card">
        <div class="check">&#x2705;</div>
        <h2>Signed in successfully</h2>
        <p>You can close this tab and return to BlueBubbles.</p>
        </div></body></html>
        """;

    public static async Task<string?> ListenForTokenAsync(string oauthUrl, CancellationToken ct = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenerPrefix);
        listener.Start();
        AppLog.Info($"OAuth: loopback listening on {ListenerPrefix}");

        using var _ = ct.Register(() =>
        {
            AppLog.Warn("OAuth: sign-in cancelled — stopping loopback listener");
            try { listener.Stop(); } catch { }
        });

        try
        {
            Process.Start(new ProcessStartInfo(oauthUrl) { UseShellExecute = true });

            while (true)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (ObjectDisposedException) { AppLog.Warn("OAuth: listener disposed before token"); return null; }
                catch (HttpListenerException) { AppLog.Warn("OAuth: listener stopped before token"); return null; }

                var path = context.Request.Url?.AbsolutePath?.TrimEnd('/');
                AppLog.Info($"OAuth: loopback request {path}");

                if (path == "/oauth/callback")
                {
                    await WriteHtmlAsync(context.Response, FragmentExtractorHtml);
                }
                else if (path == "/oauth/complete")
                {
                    var token = context.Request.QueryString["access_token"];
                    await WriteHtmlAsync(context.Response, SuccessHtml);
                    if (!string.IsNullOrEmpty(token))
                    {
                        AppLog.Info("OAuth: access token received");
                        return token;
                    }
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                }
            }
        }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.StatusCode = 200;
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }
}
