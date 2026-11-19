using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownTrellis.Plugin;

/// <summary>クリップボードの画像。IsPng なら PNG ファイルのバイト列、そうでなければ CF_DIB（BITMAPINFO＋ビット）。</summary>
internal sealed record ClipboardImage(byte[] Data, bool IsPng);

internal static class Clipboard
{
    /// <summary>
    /// テキスト（と任意でHTML Format）を設定する。他のアプリが使用中なら少し待って再試行する。
    /// メモリの確保と内容の複製はクリップボードを開く前に済ませ、失敗したら元の内容を変えずに false を返す。
    /// </summary>
    public static bool Set(nint owner, string text, byte[]? htmlFormat = null)
    {
        var items = new List<(uint Format, nint Handle)>();
        try
        {
            var textHandle = Allocate(Encoding.Unicode.GetBytes(text + '\0'));
            if (textHandle == 0)
            {
                return false;
            }

            items.Add((Native.CfUnicodeText, textHandle));
            if (htmlFormat is not null)
            {
                var withNull = new byte[htmlFormat.Length + 1];
                htmlFormat.CopyTo(withNull, 0);
                var htmlHandle = Allocate(withNull);
                if (htmlHandle == 0)
                {
                    return false;
                }

                items.Add((Native.RegisterClipboardFormat("HTML Format"), htmlHandle));
            }

            if (!Open(owner))
            {
                return false;
            }

            try
            {
                Native.EmptyClipboard();
                var ok = true;
                for (var i = 0; i < items.Count; i++)
                {
                    if (Native.SetClipboardData(items[i].Format, items[i].Handle) != 0)
                    {
                        // 所有権はクリップボードへ移ったので解放しない
                        items[i] = (items[i].Format, 0);
                    }
                    else
                    {
                        ok = false;
                    }
                }

                return ok;
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        finally
        {
            foreach (var (_, handle) in items)
            {
                if (handle != 0)
                {
                    Native.GlobalFree(handle);
                }
            }
        }
    }

    /// <summary>
    /// クリップボードの画像を複製して返す。登録形式 PNG を優先し、なければ CF_DIB。画像がなければ null。
    /// 開けなければ例外 <see cref="IOException"/>。クリップボードの内容は変えない。
    /// </summary>
    public static ClipboardImage? ReadImage(nint owner)
    {
        if (!Open(owner))
        {
            throw new IOException("The clipboard could not be opened.");
        }

        try
        {
            var png = Native.RegisterClipboardFormat("PNG");
            if (Native.IsClipboardFormatAvailable(png) && Copy(png) is { Length: > 0 } pngData)
            {
                return new ClipboardImage(pngData, true);
            }

            if (Native.IsClipboardFormatAvailable(Native.CfDib) && Copy(Native.CfDib) is { Length: > 0 } dib)
            {
                return new ClipboardImage(dib, false);
            }

            return null;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static bool Open(nint owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Native.OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(30);
        }

        return false;
    }

    private static byte[]? Copy(uint format)
    {
        var handle = Native.GetClipboardData(format);
        if (handle == 0)
        {
            return null;
        }

        var size = (long)Native.GlobalSize(handle);
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0 || size <= 0 || size > int.MaxValue)
        {
            if (pointer != 0)
            {
                Native.GlobalUnlock(handle);
            }

            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
    }

    private static nint Allocate(byte[] bytes)
    {
        var handle = Native.GlobalAlloc(Native.GmemMoveable, (nuint)bytes.Length);
        if (handle == 0)
        {
            return 0;
        }

        var pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            Native.GlobalFree(handle);
            return 0;
        }

        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Native.GlobalUnlock(handle);
        return handle;
    }
}
