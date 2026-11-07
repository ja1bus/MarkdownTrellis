using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownTrellis.Core;

public enum ImageMode
{
    /// <summary>外部画像を読み込まず、文書フォルダ配下の相対パスだけを仮想ホストへ向ける。</summary>
    Preview,

    /// <summary>許可されたURLをそのまま残す（コピー用）。</summary>
    Keep,
}

public sealed record SanitizeOptions(IReadOnlyCollection<string>? ExtraTags = null, ImageMode Images = ImageMode.Keep, string? ImageHost = null, bool PlainLinks = false);

public sealed record SanitizeResult(string Html, IReadOnlyDictionary<string, int> RejectedTags, int BlockedImages);

public sealed record HtmlAttribute(string Name, string Value);

public sealed record HtmlToken(HtmlTokenKind Kind, string Text, string Name = "", bool IsClose = false, IReadOnlyList<HtmlAttribute>? Attributes = null);

public enum HtmlTokenKind { Text, Comment, Tag }

/// <summary>
/// 許可リスト方式のHTMLサニタイザー。出力は解析結果から組み立て直すため、
/// 許可されたタグと属性以外は出力に含まれない。
/// </summary>
public static partial class HtmlSanitizer
{
    /// <summary>設定でも解除できないタグ。</summary>
    public static readonly IReadOnlySet<string> ForbiddenTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "embed", "form", "title", "textarea", "xmp", "noembed", "noframes",
        "plaintext", "base", "link", "meta", "html", "head", "body", "frame", "frameset", "applet", "svg", "math",
        "template", "noscript", "dialog", "select", "button", "video", "audio", "source", "track", "portal",
    };

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "dl", "dt", "dd", "blockquote", "pre", "code", "hr",
        "em", "strong", "del", "ins", "mark", "sub", "sup", "kbd", "abbr", "br", "a", "img", "input",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "details", "summary", "div", "span", "figure", "figcaption", "section",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img", "input", "col" };

    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.Ordinal) { "class", "id", "data-line" };

    private static readonly Dictionary<string, HashSet<string>> TagAttributes = new(StringComparer.Ordinal)
    {
        ["a"] = new(StringComparer.Ordinal) { "href", "title" },
        ["img"] = new(StringComparer.Ordinal) { "src", "alt", "title", "width", "height" },
        ["abbr"] = new(StringComparer.Ordinal) { "title" },
        ["details"] = new(StringComparer.Ordinal) { "open" },
        ["td"] = new(StringComparer.Ordinal) { "colspan", "rowspan", "style" },
        ["th"] = new(StringComparer.Ordinal) { "colspan", "rowspan", "style" },
        ["col"] = new(StringComparer.Ordinal) { "style" },
        ["ol"] = new(StringComparer.Ordinal) { "start" },
        ["input"] = new(StringComparer.Ordinal) { "type", "checked", "disabled" },
    };

    public static SanitizeResult Sanitize(string html, SanitizeOptions? options = null)
    {
        options ??= new SanitizeOptions();
        var extra = new HashSet<string>(StringComparer.Ordinal);
        if (options.ExtraTags is not null)
        {
            foreach (var tag in options.ExtraTags)
            {
                var name = tag.Trim().ToLowerInvariant();
                if (name.Length > 0 && !ForbiddenTags.Contains(name) && TagNameRegex().IsMatch(name))
                {
                    extra.Add(name);
                }
            }
        }

        var output = new StringBuilder(html.Length + 64);
        var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
        var blockedImages = 0;

        foreach (var token in Tokenize(html))
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Comment:
                    break;
                case HtmlTokenKind.Text:
                    output.Append(token.Text);
                    break;
                case HtmlTokenKind.Tag:
                    if (ForbiddenTags.Contains(token.Name) || !(AllowedTags.Contains(token.Name) || extra.Contains(token.Name)))
                    {
                        rejected[token.Name] = rejected.GetValueOrDefault(token.Name) + 1;
                        output.Append(WebUtility.HtmlEncode(token.Text));
                        break;
                    }

                    if (options.PlainLinks && token.Name == "a")
                    {
                        output.Append(token.IsClose ? "</span>" : "<span class=\"link\">");
                        break;
                    }

                    if (token.IsClose)
                    {
                        if (!VoidTags.Contains(token.Name))
                        {
                            output.Append("</").Append(token.Name).Append('>');
                        }

                        break;
                    }

                    if (!WriteOpeningTag(output, token, options, ref blockedImages))
                    {
                        rejected[token.Name] = rejected.GetValueOrDefault(token.Name) + 1;
                        output.Append(WebUtility.HtmlEncode(token.Text));
                    }

                    break;
            }
        }

        return new SanitizeResult(output.ToString(), rejected, blockedImages);
    }

    private static bool WriteOpeningTag(StringBuilder output, HtmlToken token, SanitizeOptions options, ref int blockedImages)
    {
        var attributes = new List<HtmlAttribute>();
        var isCheckbox = false;
        foreach (var attribute in token.Attributes ?? [])
        {
            var name = attribute.Name;
            if (attributes.Exists(a => a.Name == name))
            {
                continue;
            }

            var allowed = GlobalAttributes.Contains(name) || (TagAttributes.TryGetValue(token.Name, out var set) && set.Contains(name));
            if (!allowed || name.StartsWith("on", StringComparison.Ordinal))
            {
                continue;
            }

            var value = WebUtility.HtmlDecode(attribute.Value);
            switch (name)
            {
                case "href" when !IsAllowedUrl(value):
                case "src" when !IsAllowedUrl(value) || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase):
                case "style" when !AlignStyleRegex().IsMatch(value):
                case "data-line" or "colspan" or "rowspan" or "start" or "width" or "height" when !NumberRegex().IsMatch(value):
                    continue;
            }

            if (name == "type")
            {
                if (!value.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                isCheckbox = true;
                value = "checkbox";
            }

            attributes.Add(new HtmlAttribute(name, value));
        }

        if (token.Name == "input")
        {
            if (!isCheckbox)
            {
                return false;
            }

            attributes.RemoveAll(a => a.Name == "disabled");
            attributes.Add(new HtmlAttribute("disabled", string.Empty));
        }

        if (token.Name == "img" && options.Images == ImageMode.Preview)
        {
            var src = attributes.Find(a => a.Name == "src")?.Value;
            var mapped = src is null ? null : MapPreviewImage(src, options.ImageHost);
            if (mapped is null)
            {
                blockedImages++;
                var alt = attributes.Find(a => a.Name == "alt")?.Value ?? string.Empty;
                output.Append("<span class=\"blocked-image\" title=\"").Append(WebUtility.HtmlEncode(src ?? string.Empty)).Append("\">")
                    .Append(WebUtility.HtmlEncode(alt.Length > 0 ? alt : "image")).Append("</span>");
                return true;
            }

            attributes.RemoveAll(a => a.Name == "src");
            attributes.Add(new HtmlAttribute("src", mapped));
        }

        output.Append('<').Append(token.Name);
        foreach (var attribute in attributes)
        {
            output.Append(' ').Append(attribute.Name);
            if (attribute.Value.Length > 0 || attribute.Name is not ("open" or "checked" or "disabled"))
            {
                output.Append("=\"").Append(WebUtility.HtmlEncode(attribute.Value)).Append('"');
            }
        }

        output.Append('>');
        return true;
    }

    /// <summary>許可するURLか判定する。http、https、mailto、および相対パスのみ許可する。</summary>
    public static bool IsAllowedUrl(string value)
    {
        var compact = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsControl(c) && !char.IsWhiteSpace(c))
            {
                compact.Append(c);
            }
        }

        var url = compact.ToString();
        var end = url.IndexOfAny([':', '/', '?', '#', '\\']);
        if (end < 0 || url[end] != ':')
        {
            return true;
        }

        var scheme = url[..end];
        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>リンクを外部で開いてよいか判定する（相対パスは開かない）。</summary>
    public static bool IsExternalUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    /// <summary>
    /// プレビューの画像要求を、文書フォルダ配下の画像ファイルのパスへ変換する。
    /// 別のホスト、フォルダの外、画像以外の拡張子は null を返す。
    /// </summary>
    public static string? ResolveImagePath(string folder, string host, string requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0 || relative.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.GetFullPath(folder);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ImageContentTypes.ContainsKey(Path.GetExtension(full)) ? full : null;
    }

    public static string ImageContentType(string path) => ImageContentTypes[Path.GetExtension(path)];

    private static readonly Dictionary<string, string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
    };

    private static string? MapPreviewImage(string src, string? host)
    {
        if (host is null || !IsRelativeWithinRoot(src))
        {
            return null;
        }

        var path = src.Replace('\\', '/');
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").Select(s => Uri.EscapeDataString(Uri.UnescapeDataString(s)));
        return "https://" + host + "/" + string.Join('/', segments);
    }

    private static bool IsRelativeWithinRoot(string src)
    {
        if (src.Length == 0 || src[0] is '/' or '\\' or '#' || src.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var depth = 0;
        foreach (var segment in Uri.UnescapeDataString(src).Replace('\\', '/').Split('/'))
        {
            if (segment == "..")
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (segment is not ("" or "."))
            {
                depth++;
            }
        }

        return true;
    }

    /// <summary>HTMLを字句に分ける。タグとして解釈できない <c>&lt;</c> は文字として扱う。</summary>
    public static IEnumerable<HtmlToken> Tokenize(string html)
    {
        var i = 0;
        var textStart = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                if (i > textStart)
                {
                    yield return new HtmlToken(HtmlTokenKind.Text, html[textStart..i]);
                }

                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var stop = end < 0 ? html.Length : end + 3;
                yield return new HtmlToken(HtmlTokenKind.Comment, html[i..stop]);
                i = stop;
                textStart = i;
                continue;
            }

            if (TryParseTag(html, i, out var tag, out var next))
            {
                if (i > textStart)
                {
                    yield return new HtmlToken(HtmlTokenKind.Text, html[textStart..i]);
                }

                yield return tag;
                i = next;
                textStart = i;
                continue;
            }

            if (i > textStart)
            {
                yield return new HtmlToken(HtmlTokenKind.Text, html[textStart..i]);
            }

            yield return new HtmlToken(HtmlTokenKind.Text, "&lt;");
            i++;
            textStart = i;
        }

        if (i > textStart)
        {
            yield return new HtmlToken(HtmlTokenKind.Text, html[textStart..i]);
        }
    }

    private static bool TryParseTag(string html, int start, out HtmlToken token, out int next)
    {
        token = null!;
        next = start;
        var i = start + 1;
        var isClose = i < html.Length && html[i] == '/';
        if (isClose)
        {
            i++;
        }

        var nameStart = i;
        while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] is '-' or ':'))
        {
            i++;
        }

        if (i == nameStart || !char.IsAsciiLetter(html[nameStart]))
        {
            return false;
        }

        var name = html[nameStart..i].ToLowerInvariant();
        var attributes = new List<HtmlAttribute>();
        while (true)
        {
            while (i < html.Length && (char.IsWhiteSpace(html[i]) || html[i] == '/'))
            {
                i++;
            }

            if (i >= html.Length)
            {
                return false;
            }

            if (html[i] == '>')
            {
                i++;
                break;
            }

            if (html[i] is '<' or '"' or '\'' or '=')
            {
                return false;
            }

            var attrStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/' or '<' or '"' or '\''))
            {
                i++;
            }

            var attrName = html[attrStart..i].ToLowerInvariant();
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            var value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i >= html.Length)
                {
                    return false;
                }

                if (html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var close = html.IndexOf(quote, i + 1);
                    if (close < 0)
                    {
                        return false;
                    }

                    value = html[(i + 1)..close];
                    i = close + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html[valueStart..i];
                }
            }

            if (attrName.Length > 0)
            {
                attributes.Add(new HtmlAttribute(attrName, value));
            }
        }

        token = new HtmlToken(HtmlTokenKind.Tag, html[start..i], name, isClose, attributes);
        next = i;
        return true;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex(@"^text-align:\s*(left|right|center);?$", RegexOptions.IgnoreCase)]
    private static partial Regex AlignStyleRegex();

    [GeneratedRegex(@"^\d{1,7}%?$")]
    private static partial Regex NumberRegex();
}
