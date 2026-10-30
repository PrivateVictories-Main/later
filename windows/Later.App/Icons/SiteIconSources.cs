using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Later.App.Icons;

/// The window's pictures of the sites' icons: the store keeps PNG bytes, this turns each into one image, made on the UI thread the
/// first time a tile asks. Tiles ask again when `Changed` says icons arrived or left.
internal static class SiteIconSources
{
    static SiteIconStore? store;
    static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);

    /// Raised on the UI thread.
    public static event Action? Changed;

    public static void Use(SiteIconStore source, DispatcherQueue ui)
    {
        store = source;
        source.Changed += () => ui.TryEnqueue(() => { Cache.Clear(); Changed?.Invoke(); });
    }

    /// The host's icon, or null. The image fills in as it decodes (see `BitmapImage.ImageOpened`).
    public static BitmapImage? For(string host)
    {
        if (Cache.TryGetValue(host, out var known)) return known;
        if (store?.For(host) is not { } png) return null;
        var image = new BitmapImage();
        Cache[host] = image;
        _ = Load(image, png);
        return image;
    }

    static async Task Load(BitmapImage image, byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException) { }   // a bitmap that will not show: the tile keeps its letter
    }
}
