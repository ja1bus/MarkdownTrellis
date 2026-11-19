using System.Text;

namespace MarkdownTrellis.Core;

/// <summary>書き換えの前に、文書のバイト列が文字コードとして正しく読めるかを確かめる。</summary>
public static class EncodingCheck
{
    /// <summary>
    /// バイト列を厳密に（読めないバイトを置き換えずに）文字列へ戻せて、その文字列を符号化すると元と同じバイト列になるか。
    /// 置き換え文字に化けるバイトや、同じ文字に複数の符号がある並びがあると、文字位置とバイト位置の変換がずれる。
    /// </summary>
    public static bool RoundTrips(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        string text;
        try
        {
            text = Strict(encoding).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // 長さだけでなく内容も比べる（バイト数が同じでも別の符号になる並びがある）
        return encoding.GetByteCount(text) == bytes.Length && bytes.SequenceEqual(encoding.GetBytes(text));
    }

    /// <summary>読めないバイト・表せない文字で例外を投げる同じ文字コード。</summary>
    public static Encoding Strict(Encoding encoding) =>
        encoding.CodePage == 65001
            ? new UTF8Encoding(false, true)
            : Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
