using System.Runtime.InteropServices;
using MarkdownTrellis.Core;

namespace MarkdownTrellis.Plugin;

/// <summary>Notepad++のツールバーへ渡すアイコンを作る。Notepad++の終了まで保持する。</summary>
internal static unsafe class ToolbarIcon
{
    private const int StandardSize = 16;
    private const int FluentSize = 32;

    // Notepad++の標準アイコンセットは、この色を透明として扱う
    private const byte TransparentKey = 192;

    public static void Register(nint npp, int commandId)
    {
        var icons = (ToolbarIcons*)NativeMemory.AllocZeroed((nuint)sizeof(ToolbarIcons));
        icons->Bitmap = CreateStandardBitmap();
        icons->Icon = CreateIcon(false);
        icons->IconDark = CreateIcon(true);
        Native.SendMessage(npp, Native.NppmAddToolbarIconForDarkMode, commandId, (nint)icons);
    }

    private static nint CreateDib(int size, out byte* bits)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        void* pointer;
        var bitmap = Native.CreateDIBSection(0, &info, 0, &pointer, 0, 0);
        bits = (byte*)pointer;
        return bitmap;
    }

    private static nint CreateStandardBitmap()
    {
        var bitmap = CreateDib(StandardSize, out var bits);
        var pixels = IconPainter.Render(StandardSize, false);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // 半透明の画素は透明色に重ねて不透明にする
            var alpha = pixels[i + 3] / 255.0;
            for (var c = 0; c < 3; c++)
            {
                bits[i + c] = (byte)Math.Round((pixels[i + c] * alpha) + (TransparentKey * (1 - alpha)));
            }

            bits[i + 3] = 255;
        }

        return bitmap;
    }

    private static nint CreateIcon(bool dark)
    {
        var color = CreateDib(FluentSize, out var bits);
        Marshal.Copy(IconPainter.Render(FluentSize, dark), 0, (nint)bits, FluentSize * FluentSize * 4);
        var mask = Native.CreateBitmap(FluentSize, FluentSize, 1, 1, 0);
        var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
        var icon = Native.CreateIconIndirect(&info);
        Native.DeleteObject(mask);
        Native.DeleteObject(color);
        return icon;
    }
}
