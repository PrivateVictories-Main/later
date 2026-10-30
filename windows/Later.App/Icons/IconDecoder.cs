using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Later.App.Icons;

/// Whether a bitmap from a browser's icon cache really is an image that shows something: it decodes, and some pixel of it is not clear.
/// Called on a thread-pool thread, so waiting for the decoder is fine.
internal static class IconDecoder
{
    const uint Side = 16;

    public static bool Accept(byte[] data)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(data.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return false;
            var provider = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform { ScaledWidth = Side, ScaledHeight = Side },
                                                     ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var pixels = provider.DetachPixelData();
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] >= 24) return true;   // the alpha of each pixel
            return false;
        }
        catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return false;   // a bitmap that will not decode is passed over for the next one the browser has
        }
    }
}
