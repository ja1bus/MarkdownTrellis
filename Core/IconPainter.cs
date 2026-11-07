namespace MarkdownTrellis.Core;

/// <summary>ツールバーのアイコン（緑の角丸四角に白いM）を描く。画像ファイルを同梱しないため実行時に生成する。</summary>
public static class IconPainter
{
    private const int Samples = 4;
    private const double Radius = 3.0;
    private const double StrokeWidth = 2.1;

    private static readonly (double X1, double Y1, double X2, double Y2)[] Strokes =
    [
        (4, 4, 4, 12),
        (4, 4, 8, 9.2),
        (8, 9.2, 12, 4),
        (12, 4, 12, 12),
    ];

    /// <summary>上から下の順、1画素4バイト（B、G、R、A。アルファは乗算なし）の画像を返す。</summary>
    public static byte[] Render(int size, bool dark)
    {
        var (backB, backG, backR) = dark ? ((byte)0x5A, (byte)0xB5, (byte)0x2F) : ((byte)0x3E, (byte)0x8E, (byte)0x1E);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var inside = 0;
                var mark = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        // 16単位の座標で設計し、どの大きさでも同じ形にする
                        var px = (x + ((sx + 0.5) / Samples)) * 16.0 / size;
                        var py = (y + ((sy + 0.5) / Samples)) * 16.0 / size;
                        if (IsInsideBackground(px, py))
                        {
                            inside++;
                            if (IsOnMark(px, py))
                            {
                                mark++;
                            }
                        }
                    }
                }

                var offset = ((y * size) + x) * 4;
                if (inside == 0)
                {
                    continue;
                }

                var white = (double)mark / inside;
                pixels[offset] = Mix(backB, white);
                pixels[offset + 1] = Mix(backG, white);
                pixels[offset + 2] = Mix(backR, white);
                pixels[offset + 3] = (byte)Math.Round(255.0 * inside / (Samples * Samples));
            }
        }

        return pixels;
    }

    private static byte Mix(byte back, double white) => (byte)Math.Round(back + ((255 - back) * white));

    private static bool IsInsideBackground(double x, double y)
    {
        var dx = Math.Max(Math.Max(Radius - x, x - (16 - Radius)), 0);
        var dy = Math.Max(Math.Max(Radius - y, y - (16 - Radius)), 0);
        return (dx * dx) + (dy * dy) <= Radius * Radius;
    }

    private static bool IsOnMark(double x, double y)
    {
        foreach (var (x1, y1, x2, y2) in Strokes)
        {
            var vx = x2 - x1;
            var vy = y2 - y1;
            var t = Math.Clamp((((x - x1) * vx) + ((y - y1) * vy)) / ((vx * vx) + (vy * vy)), 0, 1);
            var cx = x1 + (t * vx) - x;
            var cy = y1 + (t * vy) - y;
            if ((cx * cx) + (cy * cy) <= StrokeWidth * StrokeWidth / 4)
            {
                return true;
            }
        }

        return false;
    }
}
