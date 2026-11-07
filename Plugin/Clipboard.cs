using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownTrellis.Plugin;

internal static class Clipboard
{
    /// <summary>テキスト（と任意でHTML Format）を設定する。他のアプリが使用中なら少し待って再試行する。</summary>
    public static bool Set(nint owner, string text, byte[]? htmlFormat = null)
    {
        var opened = false;
        for (var attempt = 0; attempt < 10 && !opened; attempt++)
        {
            opened = Native.OpenClipboard(owner);
            if (!opened)
            {
                Thread.Sleep(30);
            }
        }

        if (!opened)
        {
            return false;
        }

        try
        {
            Native.EmptyClipboard();
            var ok = SetBytes(Native.CfUnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
            if (htmlFormat is not null)
            {
                var withNull = new byte[htmlFormat.Length + 1];
                htmlFormat.CopyTo(withNull, 0);
                ok &= SetBytes(Native.RegisterClipboardFormat("HTML Format"), withNull);
            }

            return ok;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    private static bool SetBytes(uint format, byte[] bytes)
    {
        var handle = Native.GlobalAlloc(Native.GmemMoveable, (nuint)bytes.Length);
        if (handle == 0)
        {
            return false;
        }

        var pointer = Native.GlobalLock(handle);
        if (pointer == 0)
        {
            Native.GlobalFree(handle);
            return false;
        }

        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Native.GlobalUnlock(handle);
        if (Native.SetClipboardData(format, handle) == 0)
        {
            Native.GlobalFree(handle);
            return false;
        }

        return true;
    }
}
