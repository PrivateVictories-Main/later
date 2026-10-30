using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Later.App.Icons;

/// A browser's own icon: the shell's picture of its executable. Null when it cannot be had (the window then shows a globe).
internal static class BrowserIcon
{
    public static async Task<ImageSource?> Load(string exe)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(exe);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64, ThumbnailOptions.UseCurrentScale);
            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException) { return null; }
    }
}
