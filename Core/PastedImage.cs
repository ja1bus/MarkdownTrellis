using System.Globalization;

namespace MarkdownTrellis.Core;

/// <summary>クリップボードから貼り付けた画像の保存名と、文書に書く相対パス。</summary>
public static class PastedImage
{
    /// <summary>文書フォルダの下の保存先のフォルダ名。</summary>
    public const string FolderName = "images";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary><c>&lt;文書名&gt;-&lt;yyyyMMdd-HHmmss&gt;.png</c>。</summary>
    public static string DefaultName(string documentPath, DateTime now) =>
        Path.GetFileNameWithoutExtension(documentPath) + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png";

    /// <summary>同名があれば <c>-2</c>、<c>-3</c>… を付ける。</summary>
    public static string MakeUnique(string name, Func<string, bool> exists)
    {
        if (!exists(name))
        {
            return name;
        }

        var stem = name[..^Path.GetExtension(name).Length];
        var extension = Path.GetExtension(name);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = stem + "-" + i.ToString(CultureInfo.InvariantCulture) + extension;
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("No free file name for " + name);
    }

    /// <summary>
    /// 利用者が入力したファイル名を確かめる。フォルダを含む名前、使えない文字、予約名、末尾の空白・ピリオドは null。
    /// 拡張子 .png がなければ付ける。
    /// </summary>
    public static string? Validate(string input)
    {
        var name = input.TrimStart();
        if (name.Length == 0 || name.Length > 200 || name is "." or ".." || name.EndsWith('.') || char.IsWhiteSpace(name[^1]) ||
            name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
        {
            return null;
        }

        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            name += ".png";
        }

        var stem = name[..^4];
        if (stem.Length == 0 || stem.EndsWith(' ') || stem.EndsWith('.') || ReservedNames.Contains(stem.Split('.')[0].TrimEnd()))
        {
            return null;
        }

        return name;
    }

    /// <summary>
    /// 文書から見た画像のパス（<c>images/名前</c>）。Markdownのリンク先として名前がそのまま1回だけ復号されるよう、
    /// URLの区切り（<c>#</c>、<c>?</c>）・<c>%</c>・括弧・<c>&amp;</c> だけをパーセント符号にする（空白と日本語はそのまま）。
    /// </summary>
    public static string RelativeUrl(string name)
    {
        var builder = new System.Text.StringBuilder(FolderName.Length + 1 + name.Length);
        builder.Append(FolderName).Append('/');
        foreach (var c in name)
        {
            builder.Append(c switch
            {
                '%' => "%25",
                '#' => "%23",
                '?' => "%3F",
                '(' => "%28",
                ')' => "%29",
                '&' => "%26",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
