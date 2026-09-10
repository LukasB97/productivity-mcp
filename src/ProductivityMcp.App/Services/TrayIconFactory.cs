using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProductivityMcp.App.Services;

public enum TrayConnectionState
{
    SetupRequired,
    Connecting,
    Ready,
    Error,
}

public static class TrayIconFactory
{
    private const int Size = 32;

    public static WindowIcon Create(TrayConnectionState state)
    {
        var pixels = new byte[Size * Size * 4];

        DrawRoundedRectangle(pixels, 2, 2, 29, 29, 7, 91, 91, 214);
        DrawRectangle(pixels, 9, 7, 13, 24, 255, 255, 255);
        DrawRectangle(pixels, 12, 7, 20, 11, 255, 255, 255);
        DrawRectangle(pixels, 18, 9, 23, 17, 255, 255, 255);
        DrawRectangle(pixels, 12, 15, 20, 19, 255, 255, 255);

        DrawCircle(pixels, 25, 25, 6, 255, 255, 255);
        var statusColor = state switch
        {
            TrayConnectionState.Ready => (R: (byte)22, G: (byte)121, B: (byte)74),
            TrayConnectionState.Connecting => (R: (byte)245, G: (byte)158, B: (byte)11),
            TrayConnectionState.Error => (R: (byte)220, G: (byte)38, B: (byte)38),
            _ => (R: (byte)107, G: (byte)114, B: (byte)128),
        };
        DrawCircle(pixels, 25, 25, 4, statusColor.R, statusColor.G, statusColor.B);

        var bitmap = new WriteableBitmap(
            new PixelSize(Size, Size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var framebuffer = bitmap.Lock())
        {
            Marshal.Copy(pixels, 0, framebuffer.Address, pixels.Length);
        }

        return new WindowIcon(bitmap);
    }

    private static void DrawRoundedRectangle(
        byte[] pixels,
        int left,
        int top,
        int right,
        int bottom,
        int radius,
        byte red,
        byte green,
        byte blue)
    {
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var cornerX = x < left + radius ? left + radius : x > right - radius ? right - radius : x;
                var cornerY = y < top + radius ? top + radius : y > bottom - radius ? bottom - radius : y;
                var deltaX = x - cornerX;
                var deltaY = y - cornerY;
                if ((deltaX * deltaX) + (deltaY * deltaY) <= radius * radius)
                {
                    SetPixel(pixels, x, y, red, green, blue);
                }
            }
        }
    }

    private static void DrawRectangle(
        byte[] pixels,
        int left,
        int top,
        int right,
        int bottom,
        byte red,
        byte green,
        byte blue)
    {
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                SetPixel(pixels, x, y, red, green, blue);
            }
        }
    }

    private static void DrawCircle(
        byte[] pixels,
        int centerX,
        int centerY,
        int radius,
        byte red,
        byte green,
        byte blue)
    {
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var deltaX = x - centerX;
                var deltaY = y - centerY;
                if ((deltaX * deltaX) + (deltaY * deltaY) <= radius * radius)
                {
                    SetPixel(pixels, x, y, red, green, blue);
                }
            }
        }
    }

    private static void SetPixel(byte[] pixels, int x, int y, byte red, byte green, byte blue)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size)
        {
            return;
        }

        var offset = ((y * Size) + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = 255;
    }
}
