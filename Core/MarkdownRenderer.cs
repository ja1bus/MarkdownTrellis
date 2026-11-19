using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownTrellis.Core;

[Flags]
public enum SyntaxFeature
{
    None = 0,
    Alerts = 1,
    Footnotes = 2,
    Math = 4,
    Containers = 8,
    Highlight = 16,
    WikiLinks = 32,
    DefinitionLists = 64,
}

public sealed record RenderRequest(
    string Text,
    SyntaxFeature Features = SyntaxFeature.None,
    bool StripOuterFence = false,
    IReadOnlyCollection<string>? ExtraTags = null,
    string? ImageHost = null);

/// <param name="Document">解析した文書。外側フェンスを除外していない（位置が原文と一致する）ときだけ持つ。構文の検出で解析を共用する。</param>
public sealed record RenderResult(string Html, IReadOnlyDictionary<string, int> RejectedTags, int BlockedImages, MarkdownDocument? Document = null);

public sealed record RichCopyResult(string Html, string PlainText, int LocalImages);

public static partial class MarkdownRenderer
{
    private static readonly ConcurrentDictionary<SyntaxFeature, MarkdownPipeline> Pipelines = new();

    /// <summary>GFM（CommonMarkと表、チェックリスト、取り消し線、自動リンク）に、指定の追加構文を加えた解析器。</summary>
    public static MarkdownPipeline GetPipeline(SyntaxFeature features) =>
        Pipelines.GetOrAdd(features, static key => Build(key));

    /// <summary>プレビュー用のHTMLを生成する。原文は変更しない。</summary>
    public static RenderResult RenderPreview(RenderRequest request)
    {
        var (html, document) = ToRawHtml(request, addLineNumbers: true);
        var sanitized = HtmlSanitizer.Sanitize(html, new SanitizeOptions(request.ExtraTags, ImageMode.Preview, request.ImageHost, InteractiveCheckboxes: true));
        return new RenderResult(sanitized.Html, sanitized.RejectedTags, sanitized.BlockedImages, document);
    }

    /// <summary>IEエンジンの代替表示用のHTMLを生成する。画像は読み込まず、リンクは文字にする。</summary>
    public static string RenderLegacyPreview(RenderRequest request)
    {
        var (html, _) = ToRawHtml(request, addLineNumbers: false);
        return HtmlSanitizer.Sanitize(html, new SanitizeOptions(request.ExtraTags, ImageMode.Preview, null, PlainLinks: true)).Html;
    }

    /// <summary>書式付きコピー用のHTMLと、描画後のプレーンテキストを生成する。</summary>
    public static RichCopyResult RenderForCopy(RenderRequest request)
    {
        var (html, _) = ToRawHtml(request, addLineNumbers: false);
        var sanitized = HtmlSanitizer.Sanitize(html, new SanitizeOptions(request.ExtraTags, ImageMode.Keep, null));
        return RichHtmlConverter.Convert(sanitized.Html);
    }

    /// <summary>HTMLに変換する。外側フェンスを除外していなければ、解析した文書も返す。</summary>
    private static (string Html, MarkdownDocument? Document) ToRawHtml(RenderRequest request, bool addLineNumbers)
    {
        var text = request.Text;
        var lineOffset = 0;
        if (request.StripOuterFence)
        {
            var fence = FenceDetector.Detect(text);
            if (fence is not null)
            {
                text = text.Substring(fence.InnerStart, fence.InnerLength);
                lineOffset = fence.OpeningLine + 1;
            }
        }

        var pipeline = GetPipeline(request.Features);
        var document = Markdown.Parse(text, pipeline);
        RemoveRawLineNumbers(document);
        if (addLineNumbers)
        {
            List<int>? lineStarts = null;
            foreach (var block in document.Descendants<Block>())
            {
                if (block is ParagraphBlock or HeadingBlock or ListItemBlock or CodeBlock or QuoteBlock or ThematicBreakBlock or Table)
                {
                    block.GetAttributes().AddPropertyIfNotExist("data-line", (StartLine(block, text, ref lineStarts) + lineOffset).ToString());
                }
            }
        }

        var html = document.ToHtml(pipeline);
        return (html, ReferenceEquals(text, request.Text) ? document : null);
    }

    /// <summary>
    /// ブロックの開始行。下線形式（Setext）の見出しは Markdig の Line が下線の行になるので、見出しの文字の最初の行を使う。
    /// </summary>
    private static int StartLine(Block block, string text, ref List<int>? lineStarts)
    {
        if (block is not HeadingBlock { IsSetext: true } || block.Span.Start < 0 || block.Span.Start > text.Length)
        {
            return block.Line;
        }

        // 行の先頭の位置（CRLF・CR・LF の後ろ）を一度だけ求め、見出しの開始位置を含む行を探す
        if (lineStarts is null)
        {
            lineStarts = [0];
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
                {
                    lineStarts.Add(i + 1);
                }
            }
        }

        var index = lineStarts.BinarySearch(block.Span.Start);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>
    /// 原文の生HTMLに書かれた data-line 属性を取り除く。プレビューの data-line は自前で付けたものだけにする
    /// （ダブルクリック・追従・目次の対応先を原文の生HTMLから指定させない）。
    /// </summary>
    private static void RemoveRawLineNumbers(MarkdownDocument document)
    {
        foreach (var block in document.Descendants<HtmlBlock>())
        {
            var lines = block.Lines.Lines;
            var joined = string.Join('\n', Enumerable.Range(0, block.Lines.Count).Select(i => lines[i].Slice.ToString()));
            var cleaned = StripLineAttributes(joined);
            if (cleaned != joined)
            {
                var group = new Markdig.Helpers.StringLineGroup(4);
                foreach (var line in cleaned.Split('\n'))
                {
                    group.Add(new Markdig.Helpers.StringSlice(line));
                }

                block.Lines = group;
            }
        }

        foreach (var inline in document.Descendants<HtmlInline>())
        {
            if (inline.Tag is { } tag)
            {
                inline.Tag = StripLineAttributes(tag);
            }
        }
    }

    /// <summary>
    /// HTMLの開始タグの中の data-line 属性だけを取り除く（タグの外の文字と、他の属性の値は変えない）。
    /// 属性は引用符を区別して読むので、値の中の <c>&gt;</c> や「data-line=」という文字列では誤らない。
    /// </summary>
    public static string StripLineAttributes(string html)
    {
        if (!html.Contains("data-line", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var builder = new System.Text.StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var open = html.IndexOf('<', i);
            if (open < 0 || open + 1 >= html.Length)
            {
                builder.Append(html, i, html.Length - i);
                break;
            }

            // コメントの中はタグとして読まない
            if (string.CompareOrdinal(html, open, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                var stop = close < 0 ? html.Length : close + 3;
                builder.Append(html, i, stop - i);
                i = stop;
                continue;
            }

            if (!char.IsAsciiLetter(html[open + 1]))
            {
                builder.Append(html, i, open + 1 - i);
                i = open + 1;
                continue;
            }

            builder.Append(html, i, open - i);
            i = AppendTagWithoutLineAttributes(html, open, builder);
        }

        return builder.ToString();
    }

    /// <summary>位置 start の開始タグを、data-line 属性を除いて書き出し、タグの次の位置を返す。</summary>
    private static int AppendTagWithoutLineAttributes(string html, int start, System.Text.StringBuilder builder)
    {
        var i = start + 1;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('>' or '/'))
        {
            i++;
        }

        builder.Append(html, start, i - start);
        while (i < html.Length)
        {
            var attributeStart = i;
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= html.Length)
            {
                builder.Append(html, attributeStart, i - attributeStart);
                break;
            }

            if (html[i] == '>')
            {
                builder.Append(html, attributeStart, i + 1 - attributeStart);
                return i + 1;
            }

            if (html[i] == '/')
            {
                i++;
                builder.Append(html, attributeStart, i - attributeStart);
                continue;
            }

            var nameStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('>' or '/' or '='))
            {
                i++;
            }

            var name = html.AsSpan(nameStart, i - nameStart);
            var afterName = i;
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var close = html.IndexOf(html[i], i + 1);
                    i = close < 0 ? html.Length : close + 1;
                }
                else
                {
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }
                }
            }
            else
            {
                // 値のない属性。名前の後ろの空白は次の属性の前の空白として扱う
                i = afterName;
            }

            if (!name.Equals("data-line", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(html, attributeStart, i - attributeStart);
            }
        }

        return html.Length;
    }

    private static MarkdownPipeline Build(SyntaxFeature features)
    {
        var builder = new MarkdownPipelineBuilder();

        // 構文の検出（ProtectedRanges）が同じ解析結果の位置を使えるようにする
        builder.UsePreciseSourceLocation();
        builder.UseYamlFrontMatter();
        builder.UsePipeTables(new PipeTableOptions { UseGfmRules = true });
        builder.UseTaskLists();
        builder.UseAutoLinks();

        var emphasis = EmphasisExtraOptions.Strikethrough;

        if (features.HasFlag(SyntaxFeature.Highlight))
        {
            emphasis |= EmphasisExtraOptions.Marked;
        }

        builder.UseEmphasisExtras(emphasis);

        if (features.HasFlag(SyntaxFeature.Alerts))
        {
            // 既定の描画はSVGアイコンを出力し、サニタイザーで拒否されるためタイトルだけを出す
            builder.UseAlertBlocks(static (renderer, kind) =>
            {
                var name = kind.ToString();
                var title = name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
                renderer.Write("<p class=\"markdown-alert-title\">").WriteEscape(title).WriteLine("</p>");
            });
        }

        if (features.HasFlag(SyntaxFeature.Footnotes))
        {
            builder.UseFootnotes();
        }

        if (features.HasFlag(SyntaxFeature.Math))
        {
            builder.UseMathematics();
        }

        if (features.HasFlag(SyntaxFeature.Containers))
        {
            builder.UseCustomContainers();
        }

        if (features.HasFlag(SyntaxFeature.DefinitionLists))
        {
            builder.UseDefinitionLists();
        }

        if (features.HasFlag(SyntaxFeature.WikiLinks))
        {
            builder.Extensions.AddIfNotAlready<WikiLinkExtension>();
        }

        return builder.Build();
    }
}
