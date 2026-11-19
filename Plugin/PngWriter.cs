using System.Runtime.InteropServices;
using MarkdownTrellis.Core;
using Microsoft.Win32.SafeHandles;

namespace MarkdownTrellis.Plugin;

/// <summary>クリップボードの画像をPNGファイルとして書く。DIBはGDI+（gdiplus.dllの平坦API）で変換する。</summary>
internal static unsafe partial class PngWriter
{
    private const int PixelFormat32bppArgb = 0x0026200A;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    /// <summary>
    /// 保存するPNGのバイト列。登録形式PNGはそのまま、DIBは形式と長さを確かめてからGDI+で変換する。
    /// 対応外・不完全なDIBや変換の失敗は <see cref="InvalidDataException"/>／<see cref="IOException"/>。目的のファイルはまだ作らない。
    /// </summary>
    public static byte[] Encode(ClipboardImage image)
    {
        if (image.IsPng)
        {
            return image.Data.AsSpan(0, PngLength(image.Data)).ToArray();
        }

        var layout = DibLayout.Parse(image.Data) ?? throw new InvalidDataException("The bitmap format is not supported or the data is incomplete.");
        return EncodeDib(image.Data, layout);
    }

    /// <summary>ファイルが既にあって作れなかった例外か（呼び出し元は番号を進めて別の名前を試す）。</summary>
    public static bool IsAlreadyExists(IOException ex) => (ex.HResult & 0xFFFF) is ErrorFileExists or ErrorAlreadyExists;

    /// <summary>
    /// 新しいファイルを排他的に作って書く。既にあれば上書きせず <see cref="IOException"/>（<see cref="IsAlreadyExists"/>）。
    /// 作ったファイルの実体が root（文書フォルダ）の外にあれば（ジャンクションなど）書かずに消して <see cref="IOException"/>。
    /// 書き込みに失敗したら、この処理が作ったファイルだけを消して例外を投げる。
    /// </summary>
    public static void WriteNew(string path, byte[] png, string root)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            // 開いたハンドルで実体の位置を確かめるので、確認と書き込みの間にフォルダを差し替えられても外へは書かない
            var finalRoot = FinalDirectoryPath(root);
            var finalFile = FinalPath(stream.SafeFileHandle);
            if (!finalFile.StartsWith(finalRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The image folder points outside the document folder: " + finalFile);
            }

            stream.Write(png, 0, png.Length);
            stream.Flush(true);
            stream.Dispose();
        }
        catch
        {
            stream.Dispose();
            TryDelete(path);
            throw;
        }
    }

    /// <summary>クリップボードのメモリは切り上げた大きさのことがあるので、IENDのチャンクの後ろを捨てる。</summary>
    private static int PngLength(byte[] data)
    {
        var end = data.AsSpan().LastIndexOf("IEND"u8);
        return end >= 4 && end + 8 <= data.Length ? end + 8 : data.Length;
    }

    /// <summary>DIBをこの処理だけが使う一時ファイルへPNGで書き、そのバイト列を返す（一時ファイルは必ず消す）。</summary>
    private static byte[] EncodeDib(byte[] dib, DibLayout layout)
    {
        var temp = Path.GetTempFileName();
        try
        {
            var input = new StartupInput { Version = 1 };
            Check(GdiplusStartup(out var token, input, 0), "GdiplusStartup");
            try
            {
                fixed (byte* data = dib)
                {
                    var bits = data + layout.BitsOffset;
                    nint bitmap;
                    if (layout.HasAlpha)
                    {
                        // GdipCreateBitmapFromGdiDib はアルファを捨てるので、BGRAの画素をそのまま渡す（下から上の並びは負の行長で）
                        var scan0 = layout.TopDown ? bits : bits + ((long)layout.Stride * (layout.Height - 1));
                        var stride = layout.TopDown ? layout.Stride : -layout.Stride;
                        Check(GdipCreateBitmapFromScan0(layout.Width, layout.Height, stride, PixelFormat32bppArgb, scan0, out bitmap), "GdipCreateBitmapFromScan0");
                    }
                    else
                    {
                        Check(GdipCreateBitmapFromGdiDib(data, bits, out bitmap), "GdipCreateBitmapFromGdiDib");
                    }

                    try
                    {
                        var encoder = PngEncoder;
                        Check(GdipSaveImageToFile(bitmap, temp, &encoder, 0), "GdipSaveImageToFile");
                    }
                    finally
                    {
                        GdipDisposeImage(bitmap);
                    }
                }
            }
            finally
            {
                GdiplusShutdown(token);
            }

            return File.ReadAllBytes(temp);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string FinalDirectoryPath(string directory)
    {
        using var handle = CreateFileW(directory, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            throw new IOException("Cannot open the folder: " + directory, Marshal.GetHRForLastWin32Error());
        }

        return FinalPath(handle);
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        fixed (char* pointer = buffer)
        {
            var length = GetFinalPathNameByHandleW(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException("Cannot resolve the final path.", Marshal.GetHRForLastWin32Error());
            }

            return new string(pointer, 0, (int)length);
        }
    }

    private static void Check(int status, string name)
    {
        if (status != 0)
        {
            throw new IOException($"{name} failed: {status}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* path, uint length, uint flags);

    [LibraryImport("gdiplus")]
    private static partial int GdiplusStartup(out nuint token, in StartupInput input, nint output);

    [LibraryImport("gdiplus")]
    private static partial void GdiplusShutdown(nuint token);

    [LibraryImport("gdiplus")]
    private static partial int GdipCreateBitmapFromGdiDib(byte* info, byte* bits, out nint bitmap);

    [LibraryImport("gdiplus")]
    private static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* scan0, out nint bitmap);

    [LibraryImport("gdiplus", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GdipSaveImageToFile(nint image, string path, Guid* encoder, nint parameters);

    [LibraryImport("gdiplus")]
    private static partial int GdipDisposeImage(nint image);
}
