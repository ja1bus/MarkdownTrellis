using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax.Inlines;

namespace MarkdownTrellis.Core;

/// <summary><c>[[target]]</c> と <c>[[target|label]]</c> を通常のリンクとして解釈する部品。</summary>
public sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<WikiLinkParser>())
        {
            pipeline.InlineParsers.Insert(0, new WikiLinkParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
    }

    private sealed class WikiLinkParser : InlineParser
    {
        public WikiLinkParser() => OpeningCharacters = ['['];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var text = slice.Text;
            var start = slice.Start;
            if (start + 1 > slice.End || text[start + 1] != '[')
            {
                return false;
            }

            var close = text.IndexOf("]]", start + 2, StringComparison.Ordinal);
            if (close < 0 || close > slice.End)
            {
                return false;
            }

            var inner = text.Substring(start + 2, close - start - 2);
            if (inner.Length == 0 || inner.IndexOfAny(['\n', '\r', '[', ']']) >= 0)
            {
                return false;
            }

            var separator = inner.IndexOf('|');
            var target = (separator >= 0 ? inner[..separator] : inner).Trim();
            var label = (separator >= 0 ? inner[(separator + 1)..] : inner).Trim();
            if (target.Length == 0 || label.Length == 0)
            {
                return false;
            }

            var link = new LinkInline(Normalizer.EscapeLinkTarget(target), string.Empty);
            link.AppendChild(new LiteralInline(label));
            link.IsClosed = true;
            processor.Inline = link;
            slice.Start = close + 2;
            return true;
        }
    }
}
