using System.Buffers.Binary;

namespace MarkdownTrellis.Core;

/// <summary>
/// クリップボードの CF_DIB（BITMAPINFOHEADER／V4／V5と色表・マスク・画素）の配置。GDI+へ渡す前に、
/// 全画素が入力の中にあることと、対応する形式であることを確かめる。
/// </summary>
public sealed record DibLayout(
    int HeaderSize,
    int Width,
    int Height,
    bool TopDown,
    int BitCount,
    int Compression,
    int Colors,
    int BitsOffset,
    int Stride,
    uint RedMask,
    uint GreenMask,
    uint BlueMask,
    uint AlphaMask)
{
    public const int BiRgb = 0;
    public const int BiBitFields = 3;

    /// <summary>幅・高さの上限（ピクセル）。</summary>
    public const int MaxDimension = 32768;

    /// <summary>画素の総数の上限（約1億。32ビットで400MB）。</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>
    /// アルファのマスクが明示された32ビットのBGRA（BITMAPV4/V5HEADER＋BI_BITFIELDS、ARGBの標準のマスク）。
    /// この形式だけ透過度を保って変換する。マスクのない32ビット（BI_RGB）の上位8ビットは未使用として扱う。
    /// </summary>
    public bool HasAlpha =>
        BitCount == 32 && Compression == BiBitFields && AlphaMask == 0xFF000000 &&
        RedMask == 0x00FF0000 && GreenMask == 0x0000FF00 && BlueMask == 0x000000FF;

    /// <summary>画素の領域の大きさ（バイト）。</summary>
    public long ImageSize => (long)Stride * Height;

    /// <summary>対応する形式で、ヘッダー・色表・マスク・全走査行が入力の中にあれば配置を返す。そうでなければ null。</summary>
    public static DibLayout? Parse(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || headerSize > dib.Length)
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (width <= 0 || width > MaxDimension || rawHeight == 0 || rawHeight == int.MinValue || Math.Abs(rawHeight) > MaxDimension ||
            (long)width * Math.Abs(rawHeight) > MaxPixels || planes != 1 || bitCount is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            return null;
        }

        // RLE・JPEG・PNGの圧縮やアルファ付きのビットフィールド（BI_ALPHABITFIELDS）は扱わない
        if (compression != BiRgb && !(compression == BiBitFields && bitCount is 16 or 32))
        {
            return null;
        }

        // 上から下へ並ぶ（高さが負の）DIBは非圧縮の形式に限られる
        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);

        long colors;
        if (bitCount <= 8)
        {
            colors = colorsUsed == 0 ? 1L << bitCount : colorsUsed;
            if (colors > 1L << bitCount)
            {
                return null;
            }
        }
        else
        {
            // 16ビット以上の色表は表示の最適化用。あれば読み飛ばす
            colors = colorsUsed;
            if (colors > 256)
            {
                return null;
            }
        }

        uint red, green, blue, alpha = 0;
        long masksLength = 0;
        if (compression == BiBitFields)
        {
            // BITMAPINFOHEADER ではヘッダーの直後に3つ、V4／V5ではヘッダーの中（同じ位置）にある
            masksLength = headerSize == 40 ? 12 : 0;
            if (40 + 12 > dib.Length)
            {
                return null;
            }

            red = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
            green = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
            if (headerSize >= 56)
            {
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
            }
        }
        else if (bitCount == 16)
        {
            (red, green, blue) = (0x7C00, 0x03E0, 0x001F);
        }
        else
        {
            (red, green, blue) = (0x00FF0000, 0x0000FF00, 0x000000FF);
        }

        var bitsOffset = headerSize + masksLength + (colors * 4);
        var stride = (((long)width * bitCount) + 31) / 32 * 4;
        var imageSize = stride * height;
        if (bitsOffset > dib.Length || imageSize > dib.Length - bitsOffset)
        {
            return null;
        }

        return new DibLayout((int)headerSize, width, height, topDown, bitCount, compression, (int)colors, (int)bitsOffset, (int)stride, red, green, blue, alpha);
    }
}
