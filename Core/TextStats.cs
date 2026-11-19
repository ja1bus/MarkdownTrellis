using System.Buffers;
using System.Globalization;
using System.Text;

namespace MarkdownTrellis.Core;

/// <summary>文字数・行数・語数（語数は概算）。</summary>
public readonly record struct TextCounts(int Characters, int Lines, int Words);

/// <summary>パネルに出す文字数などを数える。Notepad++に依存しない純粋な処理。</summary>
public static class TextStats
{
    /// <summary>
    /// 文字数は改行（CR、LF）を除いたコードポイント数、行数は改行の数＋1（空なら0、CRLFは1つ）。
    /// 語数は、漢字・かな・ハングルを1文字1語、それ以外の英数字と結合文字の連続を1語と数える。
    /// </summary>
    public static TextCounts Count(string text)
    {
        if (text.Length == 0)
        {
            return new TextCounts(0, 0, 0);
        }

        var characters = 0;
        var breaks = 0;
        var words = 0;
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                if (c == '\n' || i + 1 >= text.Length || text[i + 1] != '\n')
                {
                    breaks++;
                }

                inWord = false;
                continue;
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var used) != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                used = 1;
            }

            i += used - 1;
            characters++;
            if (IsCjk(rune.Value))
            {
                words++;
                inWord = false;
            }
            else if (IsWordPart(rune))
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        return new TextCounts(characters, breaks + 1, words);
    }

    private static bool IsWordPart(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>かな・漢字・ハングル（1文字を1語と数える文字）。</summary>
    public static bool IsCjk(int c) =>
        c is (>= 0x3005 and <= 0x3007) // 々〆〇
            or (>= 0x3040 and <= 0x30FF) // ひらがな・カタカナ
            or (>= 0x31F0 and <= 0x31FF) // カタカナ拡張
            or (>= 0xFF66 and <= 0xFF9F) // 半角カナ
            or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x3FFFF) // 漢字
            or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F) or (>= 0xAC00 and <= 0xD7AF); // ハングル
}
