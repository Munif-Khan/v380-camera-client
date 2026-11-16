using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Java.Interop;
using V380Cameras.App;
using V380Cameras.Http;
using V380Decoder.src;

namespace V380Cameras;

/// <summary>
/// A single-screen shell around the bundled web UI.
///
/// The manager and camera pages are served from loopback by
/// <see cref="CameraManager"/>, so this activity only has to host a WebView and
/// bridge the two things a WebView cannot do on its own: save a blob photo and
/// save a streaming HTTP file to the phone's Downloads folder.
/// </summary>
[Activity(
    Name = "local.v380.cameras.MainActivity",
    Label = "V380 Cameras",
    Icon = "@drawable/ic_launcher",
    Theme = "@style/AppTheme",
    LaunchMode = LaunchMode.SingleTask,
    Exported = true,
    HardwareAccelerated = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.ScreenLayout |
        ConfigChanges.UiMode |
        ConfigChanges.KeyboardHidden)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher })]
public class MainActivity : Activity
{
    private WebView? web;
    private ShellChromeClient? chrome;
    private View? customView;
    private CameraManager? manager;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        LogUtils.enableDebug = false;

        // One store, one manager, one WebView. Everything the camera needs is
        // in process; no PC and no companion service.
        var store = new CameraStore(FilesDir!.AbsolutePath);
        manager = new CameraManager(store, CacheDir!.AbsolutePath);
        manager.Start();

        web = new WebView(this);
        SetContentView(web, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var settings = web.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.SetSupportZoom(false);
        settings.BuiltInZoomControls = false;
        settings.DisplayZoomControls = false;
        settings.AllowFileAccess = false;
        settings.AllowContentAccess = true;
        settings.CacheMode = CacheModes.NoCache;
        settings.MediaPlaybackRequiresUserGesture = true;
        settings.UserAgentString = settings.UserAgentString + " V380Cameras/1.0";
        web.SetBackgroundColor(Color.ParseColor("#0b0f14"));

        chrome = new ShellChromeClient(this);
        web.SetWebChromeClient(chrome);
        web.SetWebViewClient(new WebViewClient());
        web.AddJavascriptInterface(new PhotoBridge(this), "AndroidCameras");
        web.SetDownloadListener(new ClipDownloadListener(this));

        web.LoadUrl(manager.Url);
    }

    protected override void OnResume()
    {
        base.OnResume();
        web?.ResumeTimers();
        // The stream is only useful while the app is on screen; leaving it open
        // in the background drains battery and holds the camera's one socket.
        manager?.SetActive(true);
    }

    protected override void OnPause()
    {
        manager?.SetActive(false);
        web?.PauseTimers();
        base.OnPause();
    }

    public override void OnBackPressed()
    {
        if (customView != null)
        {
            chrome?.HideCustomView();
            return;
        }

        if (web != null && web.CanGoBack())
        {
            web.GoBack();
            return;
        }

        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        // The process is going away; do not block the UI thread waiting for
        // sockets to unwind, just start the shutdown.
        _ = manager?.DisposeAsync();
        try { web?.Destroy(); } catch (Exception) { }
        base.OnDestroy();
    }

    /// <summary>Toasts must be raised on a thread with a Looper.</summary>
    private void Notify(string message) => RunOnUiThread(() =>
        Toast.MakeText(this, message, ToastLength.Long)?.Show());

    internal void ShowCustomView(View view)
    {
        if (customView != null) return;
        customView = view;

        var root = web?.Parent as ViewGroup;
        if (root == null) return;

        root.AddView(view, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        web!.Visibility = ViewStates.Gone;
        Window?.AddFlags(WindowManagerFlags.Fullscreen);
    }

    internal void HideCustomView()
    {
        var view = customView;
        customView = null;
        if (view == null) return;

        var root = web?.Parent as ViewGroup;
        root?.RemoveView(view);
        if (web != null) web.Visibility = ViewStates.Visible;
        Window?.ClearFlags(WindowManagerFlags.Fullscreen);
    }

    internal void ShowConfirm(string message, Action<bool> done)
    {
        RunOnUiThread(() =>
        {
            var builder = new AlertDialog.Builder(this);
            builder.SetMessage(message);
            builder.SetPositiveButton("OK", (_, _) => done(true));
            builder.SetNegativeButton("Cancel", (_, _) => done(false));
            var dialog = builder.Create() ?? throw new InvalidOperationException("Could not show the dialog");
            dialog.Show();
        });
    }

    internal void ShowAlert(string message)
    {
        RunOnUiThread(() =>
        {
            var builder = new AlertDialog.Builder(this);
            builder.SetMessage(message);
            builder.SetPositiveButton("OK", (_, _) => { });
            builder.Create()?.Show();
        });
    }

    /// <summary>Called from the page's JavaScript when a photo is captured.</summary>
    internal void SavePhoto(string dataUrl, string name)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) throw new FormatException("Not a data URL");
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);

            var (stream, done) = CreateSink(SafeName(name, ".jpg"), "image/jpeg", image: true);
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                done(true);
            }
            catch
            {
                done(false);
                throw;
            }

            Notify("Photo saved to Pictures/V380");
        }
        catch (Exception ex)
        {
            Notify("Could not save the photo: " + ex.Message);
        }
    }

    /// <summary>
    /// Runs the clip request again in the background and streams it to the
    /// phone's storage. The WebView never sees the body, so the SD clip is
    /// downloaded exactly once.
    /// </summary>
    internal async Task SaveClipAsync(string url, string disposition)
    {
        var name = SafeName(FilenameFromDisposition(disposition), ".mp4");
        Notify("Saving clip. This can take as long as the clip plays.");

        try
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var (stream, done) = CreateSink(name, "video/mp4", image: false);
            try
            {
                await response.Content.CopyToAsync(stream);
                done(true);
            }
            catch
            {
                done(false);
                throw;
            }

            Notify("Clip saved as " + name);
        }
        catch (Exception ex)
        {
            Notify("Could not save the clip: " + ex.Message);
        }
    }

    private (Stream Stream, Action<bool> Done) CreateSink(string name, string mime, bool image)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, name);
            values.Put(MediaStore.IMediaColumns.MimeType, mime);
            values.Put(MediaStore.IMediaColumns.RelativePath,
                (image ? Android.OS.Environment.DirectoryPictures : Android.OS.Environment.DirectoryDownloads) + "/V380");
            values.Put(MediaStore.IMediaColumns.IsPending, 1);

            var collection = (image
                ? MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary)
                : MediaStore.Downloads.ExternalContentUri)
                ?? throw new IOException("The phone did not offer a shared folder");

            var resolver = ContentResolver ?? throw new IOException("The phone did not provide a content resolver");

            var uri = resolver.Insert(collection, values)
                ?? throw new IOException("The phone did not create the file");
            var stream = resolver.OpenOutputStream(uri)
                ?? throw new IOException("The phone did not open the file");

            return (stream, ok =>
            {
                try { stream.Flush(); stream.Dispose(); } catch (Exception) { }
                if (!ok)
                {
                    try { resolver.Delete(uri, null, null); } catch (Exception) { }
                    return;
                }
                var done = new ContentValues();
                done.Put(MediaStore.IMediaColumns.IsPending, 0);
                try { resolver.Update(uri, done, null, null); } catch (Exception) { }
            });
        }

        // Android 9 and older cannot publish to shared storage without a runtime
        // permission prompt, so the file goes to the app's own folder. It is
        // reachable from a file manager and survives app updates.
        var directory = new Java.IO.File(GetExternalFilesDir(null), "V380");
        directory.Mkdirs();
        var file = new Java.IO.File(directory, name);
        var output = File.Create(file.AbsolutePath);
        return (output, ok =>
        {
            try { output.Dispose(); } catch (Exception) { }
            if (!ok)
            {
                try { File.Delete(file.AbsolutePath); } catch (Exception) { }
            }
        });
    }

    private static string FilenameFromDisposition(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition)) return "";
        var marker = disposition!.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return "";
        var value = disposition[(marker + "filename=".Length)..].Trim().Trim('"');
        var semicolon = value.IndexOf(';');
        return (semicolon < 0 ? value : value[..semicolon]).Trim();
    }

    /// <summary>
    /// Keeps a file name to something MediaStore accepts, and never empty.
    /// </summary>
    private static string SafeName(string? proposed, string extension)
    {
        var name = (proposed ?? "").Trim();
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(bad, '-');
        name = name.Replace('/', '-').Replace('\\', '-');

        if (name.Length == 0) name = "v380-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (name.Length > 96) name = name[..96];
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;
        return name;
    }

    /// <summary>Exposed to JavaScript as <c>window.AndroidCameras</c>.</summary>
    private sealed class PhotoBridge : Java.Lang.Object
    {
        private readonly MainActivity activity;
        public PhotoBridge(MainActivity activity) => this.activity = activity;

        [JavascriptInterface]
        [Export("savePhoto")]
        public void SavePhoto(string dataUrl, string name) => activity.SavePhoto(dataUrl, name);
    }

    private sealed class ClipDownloadListener : Java.Lang.Object, IDownloadListener
    {
        private readonly MainActivity activity;
        public ClipDownloadListener(MainActivity activity) => this.activity = activity;

        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength)
        {
            if (string.IsNullOrEmpty(url)) return;
            _ = activity.SaveClipAsync(url!, contentDisposition ?? "");
        }
    }

    private sealed class ShellChromeClient : WebChromeClient
    {
        private readonly MainActivity activity;
        private WebChromeClient.ICustomViewCallback? customViewCallback;

        public ShellChromeClient(MainActivity activity) => this.activity = activity;

        public override void OnShowCustomView(View? view, ICustomViewCallback? callback)
        {
            if (view == null) return;
            customViewCallback = callback;
            activity.ShowCustomView(view);
        }

        public override void OnHideCustomView()
        {
            HideCustomView();
        }

        public void HideCustomView()
        {
            activity.HideCustomView();
            customViewCallback?.OnCustomViewHidden();
            customViewCallback = null;
        }

        public override bool OnJsConfirm(WebView? view, string? url, string? message, JsResult? result)
        {
            activity.ShowConfirm(message ?? "", ok =>
            {
                if (ok) result?.Confirm();
                else result?.Cancel();
            });
            return true;
        }

        public override bool OnJsAlert(WebView? view, string? url, string? message, JsResult? result)
        {
            activity.ShowAlert(message ?? "");
            result?.Confirm();
            return true;
        }
    }
}




