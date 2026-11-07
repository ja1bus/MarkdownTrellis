using System.Net;
using System.Text;

namespace MarkdownTrellis.Core;

/// <summary>サニタイズ済みHTMLを、貼り付け先で崩れにくいHTMLとプレーンテキストへ変換する。</summary>
public static class RichHtmlConverter
{
    private const int BulletList = -1;
    private const string CodeFont ="font-family:Consolas,'Courier New',monospace;";

    private static readonly Dictionary<string, string> Styles = new(StringComparer.Ordinal)
    {
        ["table"] = "border-collapse:collapse;",
        ["th"] = "border:1px solid #999;padding:4px 8px;background:#f2f2f2;font-weight:bold;",
        ["td"] = "border:1px solid #999;padding:4px 8px;",
        ["pre"] = "background:#f5f5f5;padding:8px;" + CodeFont,
        ["code"] = CodeFont,
        ["kbd"] = CodeFont,
        ["blockquote"] = "border-left:4px solid #ccc;margin:0 0 8px 0;padding-left:12px;color:#555;",
    };

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "blockquote", "table", "ul", "ol", "dl", "div", "section", "figure", "summary", "details",
    };

    public static RichCopyResult Convert(string sanitizedHtml)
    {
        var html = new StringBuilder(sanitizedHtml.Length + 256);
        var text = new StringBuilder(sanitizedHtml.Length);
        var localImages = 0;
        var lists = new Stack<int>();
        var cellInRow = 0;
        var preDepth = 0;
        var afterMarker = false;

        foreach (var token in HtmlSanitizer.Tokenize(sanitizedHtml))
        {
            if (token.Kind == HtmlTokenKind.Text)
            {
                html.Append(token.Text);
                var decoded = WebUtility.HtmlDecode(token.Text);
                afterMarker &= string.IsNullOrWhiteSpace(decoded);
                if (preDepth > 0)
                {
                    text.Append(decoded);
                }
                else if (string.IsNullOrWhiteSpace(decoded) && decoded.Contains('\n'))
                {
                    // タグの間の改行は表示されないので含めない
                }
                else
                {
                    text.Append(decoded.Replace('\n', ' '));
                }

                continue;
            }

            if (token.Kind != HtmlTokenKind.Tag)
            {
                continue;
            }

            var name = token.Name;
            if (token.IsClose)
            {
                WriteClose(name, html, text, lists, ref preDepth);
                continue;
            }

            switch (name)
            {
                case "details":
                    EnsureNewLine(text);
                    continue;
                case "summary":
                    html.Append("<p><strong>");
                    EnsureNewLine(text);
                    continue;
                case "input":
                    var box = token.Attributes!.Any(a => a.Name == "checked") ? "☑ " : "☐ ";
                    html.Append(box);
                    text.Append(box);
                    continue;
                case "img":
                    WriteImage(token, html, text, ref localImages);
                    continue;
                case "br":
                    html.Append("<br>");
                    text.Append('\n');
                    continue;
                case "hr":
                    html.Append("<hr>");
                    EnsureNewLine(text);
                    text.Append("---\n");
                    continue;
                case "ul":
                    lists.Push(BulletList);
                    EnsureNewLine(text);
                    break;
                case "ol":
                    var start = token.Attributes!.FirstOrDefault(a => a.Name == "start")?.Value;
                    lists.Push(int.TryParse(start, out var first) && first >= 0 ? first : 1);
                    EnsureNewLine(text);
                    break;
                case "li":
                    EnsureNewLine(text);
                    if (lists.Count > 0)
                    {
                        text.Append(' ', (lists.Count - 1) * 2);
                        var number = lists.Pop();
                        text.Append(number == BulletList ? "- " : number + ". ");
                        lists.Push(number == BulletList ? BulletList : number + 1);
                        afterMarker = true;
                    }

                    break;
                case "tr":
                    EnsureNewLine(text);
                    cellInRow = 0;
                    break;
                case "td" or "th":
                    if (cellInRow++ > 0)
                    {
                        text.Append('\t');
                    }

                    break;
                case "pre":
                    preDepth++;
                    EnsureNewLine(text);
                    break;
                default:
                    // 項目の先頭の段落は記号と同じ行に続ける
                    if (BlockTags.Contains(name) && !afterMarker)
                    {
                        EnsureNewLine(text);
                    }

                    break;
            }

            html.Append('<').Append(name);
            WriteAttributes(token, html);
            html.Append('>');
        }

        return new RichCopyResult(html.ToString(), text.ToString().Trim('\n'), localImages);
    }

    private static void WriteClose(string name, StringBuilder html, StringBuilder text, Stack<int> lists, ref int preDepth)
    {
        switch (name)
        {
            case "details":
                return;
            case "summary":
                html.Append("</strong></p>");
                EnsureNewLine(text);
                return;
            case "ul" or "ol":
                if (lists.Count > 0)
                {
                    lists.Pop();
                }

                break;
            case "pre":
                preDepth = Math.Max(0, preDepth - 1);
                break;
        }

        html.Append("</").Append(name).Append('>');
        if (BlockTags.Contains(name) || name is "li" or "tr")
        {
            EnsureNewLine(text);
        }
    }

    private static void WriteImage(HtmlToken token, StringBuilder html, StringBuilder text, ref int localImages)
    {
        var src = token.Attributes!.FirstOrDefault(a => a.Name == "src")?.Value ?? string.Empty;
        var alt = WebUtility.HtmlDecode(token.Attributes!.FirstOrDefault(a => a.Name == "alt")?.Value ?? string.Empty);
        var decodedSrc = WebUtility.HtmlDecode(src);
        if (decodedSrc.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || decodedSrc.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            html.Append("<img src=\"").Append(WebUtility.HtmlEncode(decodedSrc)).Append("\" alt=\"").Append(WebUtility.HtmlEncode(alt)).Append("\">");
            text.Append(alt);
            return;
        }

        localImages++;
        html.Append(WebUtility.HtmlEncode(alt));
        text.Append(alt);
    }

    private static void WriteAttributes(HtmlToken token, StringBuilder html)
    {
        var style = Styles.GetValueOrDefault(token.Name, string.Empty);
        foreach (var attribute in token.Attributes!)
        {
            var value = WebUtility.HtmlDecode(attribute.Value);
            switch (attribute.Name)
            {
                case "href":
                    if (HtmlSanitizer.IsExternalUrl(value))
                    {
                        html.Append(" href=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
                    }

                    break;
                case "style":
                    style += value.TrimEnd(';') + ";";
                    break;
                case "title" or "colspan" or "rowspan" or "start":
                    html.Append(' ').Append(attribute.Name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
                    break;
            }
        }

        if (style.Length > 0)
        {
            html.Append(" style=\"").Append(WebUtility.HtmlEncode(style)).Append('"');
        }
    }

    private static void EnsureNewLine(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }
    }

    /// <summary>Windowsクリップボードの HTML Format の文字列（UTF-8のバイト位置を含む）を組み立てる。</summary>
    public static byte[] BuildClipboardHtml(string fragment)
    {
        const string StartMarker = "<!--StartFragment-->";
        const string EndMarker = "<!--EndFragment-->";
        const string HeaderFormat = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string Prefix = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" + StartMarker;
        const string Suffix = EndMarker + "</body></html>";

        var encoding = new UTF8Encoding(false);
        var headerLength = encoding.GetByteCount(string.Format(HeaderFormat, 0, 0, 0, 0));
        var startFragment = headerLength + encoding.GetByteCount(Prefix);
        var endFragment = startFragment + encoding.GetByteCount(fragment);
        var endHtml = endFragment + encoding.GetByteCount(Suffix);
        return encoding.GetBytes(string.Format(HeaderFormat, headerLength, endHtml, startFragment, endFragment) + Prefix + fragment + Suffix);
    }
}
