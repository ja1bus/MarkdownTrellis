using System.Collections.Concurrent;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Renderers.Html;
using Markdig.Syntax;

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

public sealed record RenderResult(string Html, IReadOnlyDictionary<string, int> RejectedTags, int BlockedImages);

public sealed record RichCopyResult(string Html, string PlainText, int LocalImages);

public static class MarkdownRenderer
{
    private static readonly ConcurrentDictionary<SyntaxFeature, MarkdownPipeline> Pipelines = new();

    /// <summary>GFM（CommonMarkと表、チェックリスト、取り消し線、自動リンク）に、指定の追加構文を加えた解析器。</summary>
    public static MarkdownPipeline GetPipeline(SyntaxFeature features) =>
        Pipelines.GetOrAdd(features, static key => Build(key));

    /// <summary>プレビュー用のHTMLを生成する。原文は変更しない。</summary>
    public static RenderResult RenderPreview(RenderRequest request)
    {
        var (html, _) = ToRawHtml(request, addLineNumbers: true);
        var sanitized = HtmlSanitizer.Sanitize(html, new SanitizeOptions(request.ExtraTags, ImageMode.Preview, request.ImageHost));
        return new RenderResult(sanitized.Html, sanitized.RejectedTags, sanitized.BlockedImages);
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

    private static (string Html, int LineOffset) ToRawHtml(RenderRequest request, bool addLineNumbers)
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
        if (addLineNumbers)
        {
            foreach (var block in document.Descendants<Block>())
            {
                if (block is ParagraphBlock or HeadingBlock or ListItemBlock or CodeBlock or QuoteBlock or ThematicBreakBlock or Table)
                {
                    block.GetAttributes().AddPropertyIfNotExist("data-line", (block.Line + lineOffset).ToString());
                }
            }
        }

        return (document.ToHtml(pipeline), lineOffset);
    }

    private static MarkdownPipeline Build(SyntaxFeature features)
    {
        var builder = new MarkdownPipelineBuilder();
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
