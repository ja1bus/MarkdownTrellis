using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownTrellis.Core;

/// <summary>プレビューと同じ解析で求めた、変更してはいけない範囲（コードとFrontmatter）。</summary>
public sealed class ProtectedRanges
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UseYamlFrontMatter()
        .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    private readonly List<(int Start, int End)> _ranges = [];

    private ProtectedRanges()
    {
    }

    public static ProtectedRanges Find(string text)
    {
        var result = new ProtectedRanges();
        var document = Markdown.Parse(text, Pipeline);

        // Frontmatterのブロックも CodeBlock の派生なのでここに含まれる
        foreach (var block in document.Descendants<CodeBlock>())
        {
            result.Add(block.Span);

            // 閉じていないフェンスは親の終わりまで続く（ソース位置には最初の行しか含まれない場合がある）
            if (block is FencedCodeBlock { ClosingFencedCharCount: 0 } fenced)
            {
                var end = fenced.Parent is null or MarkdownDocument ? text.Length - 1 : fenced.Parent.Span.End;
                result.Add(new SourceSpan(fenced.Span.Start, Math.Max(fenced.Span.End, end)));
            }
        }

        foreach (var inline in document.Descendants<CodeInline>())
        {
            result.Add(inline.Span);
        }

        return result;
    }

    public bool Intersects(int start, int length)
    {
        var end = start + Math.Max(length, 1);
        return _ranges.Exists(r => r.Start < end && start < r.End);
    }

    private void Add(SourceSpan span)
    {
        if (!span.IsEmpty)
        {
            _ranges.Add((span.Start, span.End + 1));
        }
    }
}

public sealed record FenceInfo(int OpeningLine, int InnerStart, int InnerLength, int OpeningStart, int OpeningLength, int ClosingStart, int ClosingLength, bool IsAmbiguous);

/// <summary>文書全体を囲むコードフェンスを検出する。</summary>
public static partial class FenceDetector
{
    public static FenceInfo? Detect(string text)
    {
        var lines = LineSplitter.Split(text);
        var first = lines.FindIndex(l => !string.IsNullOrWhiteSpace(l.Content));
        var last = lines.FindLastIndex(l => !string.IsNullOrWhiteSpace(l.Content));
        if (first < 0 || last <= first)
        {
            return null;
        }

        var open = OpeningRegex().Match(lines[first].Content);
        if (!open.Success)
        {
            return null;
        }

        var fence = open.Groups[1].Value;
        var close = ClosingRegex().Match(lines[last].Content);
        if (!close.Success || close.Groups[1].Value[0] != fence[0] || close.Groups[1].Length < fence.Length)
        {
            return null;
        }

        // 内側に同じ種類の閉じられるフェンスがある場合や、言語指定がない場合は断定しない
        var innerRuns = 0;
        for (var i = first + 1; i < last; i++)
        {
            var inner = AnyFenceRegex().Match(lines[i].Content);
            if (inner.Success && inner.Groups[1].Value[0] == fence[0] && inner.Groups[1].Length >= fence.Length)
            {
                innerRuns++;
            }
        }

        var hasLanguage = open.Groups[2].Success && open.Groups[2].Length > 0;
        var ambiguous = !hasLanguage || innerRuns % 2 != 0;
        var innerStart = lines[first].End;
        return new FenceInfo(
            first,
            innerStart,
            lines[last].Start - innerStart,
            lines[first].Start,
            lines[first].End - lines[first].Start,
            lines[last].Start,
            lines[last].End - lines[last].Start,
            ambiguous);
    }

    /// <summary>外側のフェンス行だけを取り除いた文書を返す。</summary>
    public static string Unwrap(string text, FenceInfo fence) =>
        text[..fence.OpeningStart] + text.Substring(fence.InnerStart, fence.InnerLength) + text[(fence.ClosingStart + fence.ClosingLength)..];

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})[ \t]*(markdown|md|gfm|commonmark)?[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningRegex();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})[ \t]*$")]
    private static partial Regex ClosingRegex();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex AnyFenceRegex();
}

public sealed record DetectedSyntax(SyntaxFeature Feature, int Count);

/// <summary>追加構文の候補を根拠の件数とともに検出する。方言名は断定しない。</summary>
public static partial class SyntaxDetector
{
    public static IReadOnlyList<DetectedSyntax> Detect(string text, SyntaxFeature active = SyntaxFeature.None)
    {
        var counts = new Dictionary<SyntaxFeature, int>();
        var ranges = ProtectedRanges.Find(text);
        foreach (var line in LineSplitter.Split(text))
        {
            Count(counts, SyntaxFeature.Alerts, AlertRegex(), line, ranges);
            Count(counts, SyntaxFeature.Footnotes, FootnoteRegex(), line, ranges);
            Count(counts, SyntaxFeature.Math, DisplayMathRegex(), line, ranges);
            Count(counts, SyntaxFeature.Math, BracketMathRegex(), line, ranges);
            Count(counts, SyntaxFeature.Math, ParenMathRegex(), line, ranges);
            Count(counts, SyntaxFeature.Containers, ContainerRegex(), line, ranges);
            Count(counts, SyntaxFeature.Highlight, HighlightRegex(), line, ranges);
            Count(counts, SyntaxFeature.WikiLinks, WikiRegex(), line, ranges);
            Count(counts, SyntaxFeature.DefinitionLists, DefinitionRegex(), line, ranges);
        }

        return counts.Where(p => p.Value > 0 && !active.HasFlag(p.Key)).OrderBy(p => (int)p.Key).Select(p => new DetectedSyntax(p.Key, p.Value)).ToList();
    }

    private static void Count(Dictionary<SyntaxFeature, int> counts, SyntaxFeature feature, Regex regex, SourceLine line, ProtectedRanges ranges)
    {
        foreach (Match match in regex.Matches(line.Content))
        {
            if (!ranges.Intersects(line.Start + match.Index, match.Length))
            {
                counts[feature] = counts.GetValueOrDefault(feature) + 1;
            }
        }
    }

    [GeneratedRegex(@"^\s*>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]", RegexOptions.IgnoreCase)]
    private static partial Regex AlertRegex();

    [GeneratedRegex(@"\[\^[^\]\s]+\]")]
    private static partial Regex FootnoteRegex();

    [GeneratedRegex(@"^\s*\$\$")]
    private static partial Regex DisplayMathRegex();

    [GeneratedRegex(@"^\s*\\\[\s*$")]
    private static partial Regex BracketMathRegex();

    [GeneratedRegex(@"\\\(.+?\\\)")]
    private static partial Regex ParenMathRegex();

    [GeneratedRegex(@"^:::+[ \t]*\w")]
    private static partial Regex ContainerRegex();

    [GeneratedRegex(@"==[^=\s](?:[^=\n]*[^=\s])?==")]
    private static partial Regex HighlightRegex();

    [GeneratedRegex(@"\[\[[^\[\]\n]+\]\]")]
    private static partial Regex WikiRegex();

    [GeneratedRegex(@"^:[ \t]+\S")]
    private static partial Regex DefinitionRegex();
}

public sealed record LineChange(int Line, string Before, string After);

public sealed record NormalizationResult(string Text, IReadOnlyList<LineChange> Changes, bool MayLoseMeaning);

/// <summary>構文単位の明示的な変換。コードブロック内と変換できない構文は変更しない。</summary>
public static partial class Normalizer
{
    public static IReadOnlyList<SyntaxFeature> Supported { get; } = [SyntaxFeature.Math, SyntaxFeature.WikiLinks, SyntaxFeature.Containers];

    public static NormalizationResult Apply(string text, SyntaxFeature feature)
    {
        var output = new StringBuilder(text.Length);
        var changes = new List<LineChange>();
        var lines = LineSplitter.Split(text);
        var ranges = ProtectedRanges.Find(text);
        var containerDepth = 0;
        var mayLose = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var before = line.Content;
            var after = before;
            var isProtected = ranges.Intersects(line.Start, before.Length);
            switch (feature)
            {
                case SyntaxFeature.Math:
                    after = ConvertMath(line, isProtected, ranges);
                    break;
                case SyntaxFeature.WikiLinks:
                    after = ReplaceOutside(WikiRegex(), line, ranges, ConvertWiki);
                    break;
                case SyntaxFeature.Containers:
                    var open = ContainerOpenRegex().Match(before);
                    if (!isProtected && containerDepth > 0 && ContainerCloseRegex().IsMatch(before))
                    {
                        containerDepth--;
                        after = string.Empty;
                    }
                    else if (!isProtected && open.Success && open.Groups[1].Length > 0)
                    {
                        containerDepth++;
                        mayLose = true;
                        after = $"> **{open.Groups[1].Value}**";
                    }
                    else if (containerDepth > 0)
                    {
                        // コンテナ内のコードも引用の中へ移す（コードの内容は変わらない）
                        after = before.Length > 0 ? "> " + before : ">";
                    }

                    break;
            }

            if (after != before)
            {
                changes.Add(new LineChange(index + 1, before, after));
            }

            output.Append(after).Append(line.Eol);
        }

        return new NormalizationResult(output.ToString(), changes, mayLose);
    }

    public static string EscapeLinkTarget(string target) => target.Replace(" ", "%20", StringComparison.Ordinal);

    private static string ConvertMath(SourceLine line, bool isProtected, ProtectedRanges ranges)
    {
        var trimmed = line.Content.Trim();
        if (!isProtected && trimmed is "\\[" or "\\]")
        {
            return line.Content.Replace(trimmed, "$$", StringComparison.Ordinal);
        }

        return ReplaceOutside(ParenMathRegex(), line, ranges, m => "$" + m.Groups[1].Value.Trim() + "$");
    }

    private static string ReplaceOutside(Regex regex, SourceLine line, ProtectedRanges ranges, Func<Match, string> convert) =>
        regex.Replace(line.Content, m => ranges.Intersects(line.Start + m.Index, m.Length) ? m.Value : convert(m));

    private static string ConvertWiki(Match match)
    {
        var inner = match.Groups[1].Value;
        var separator = inner.IndexOf('|');
        var target = (separator >= 0 ? inner[..separator] : inner).Trim();
        var label = (separator >= 0 ? inner[(separator + 1)..] : inner).Trim();
        return target.Length == 0 || label.Length == 0 ? match.Value : $"[{label}]({EscapeLinkTarget(target)})";
    }

    [GeneratedRegex(@"\\\((.+?)\\\)")]
    private static partial Regex ParenMathRegex();

    [GeneratedRegex(@"\[\[([^\[\]\n]+)\]\]")]
    private static partial Regex WikiRegex();

    [GeneratedRegex(@"^:::+[ \t]*(\w*).*$")]
    private static partial Regex ContainerOpenRegex();

    [GeneratedRegex(@"^:::+[ \t]*$")]
    private static partial Regex ContainerCloseRegex();
}

public readonly record struct SourceLine(int Start, int ContentEnd, int End, string Content, string Eol);

public readonly record struct CodeBlockRange(int Open, int Close, string Fence);

public static partial class LineSplitter
{
    public static List<SourceLine> Split(string text)
    {
        var lines = new List<SourceLine>();
        var start = 0;
        while (start <= text.Length)
        {
            var i = start;
            while (i < text.Length && text[i] is not ('\r' or '\n'))
            {
                i++;
            }

            var eolLength = i >= text.Length ? 0 : (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1);
            if (i >= text.Length && start == text.Length && lines.Count > 0)
            {
                lines.Add(new SourceLine(start, start, start, string.Empty, string.Empty));
                break;
            }

            lines.Add(new SourceLine(start, i, i + eolLength, text[start..i], text.Substring(i, eolLength)));
            if (eolLength == 0)
            {
                break;
            }

            start = i + eolLength;
        }

        return lines;
    }

    /// <summary>コードブロックの範囲を返す。閉じていないブロックの Close は -1。</summary>
    public static List<CodeBlockRange> FindCodeBlocks(IReadOnlyList<SourceLine> lines)
    {
        var blocks = new List<CodeBlockRange>();
        var open = -1;
        var fence = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            var match = FenceRegex().Match(lines[i].Content);
            if (!match.Success)
            {
                continue;
            }

            var run = match.Groups[1].Value;
            if (open < 0)
            {
                open = i;
                fence = run;
            }
            else if (run[0] == fence[0] && run.Length >= fence.Length && match.Groups[2].Value.Trim().Length == 0)
            {
                blocks.Add(new CodeBlockRange(open, i, fence));
                open = -1;
            }
        }

        if (open >= 0)
        {
            blocks.Add(new CodeBlockRange(open, -1, fence));
        }

        return blocks;
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceRegex();
}

/// <summary>選択範囲だけを書式付きコピーするときの確認。</summary>
public static partial class FragmentCheck
{
    /// <summary>
    /// 断片が、断片の外（文書の他の場所）にだけある参照リンクや脚注の定義を使っているかを調べる。
    /// 通知のための目安で、コードブロック内の記述なども数える。
    /// </summary>
    public static bool UsesOutsideDefinitions(string document, string fragment, SyntaxFeature features)
    {
        var inside = Labels(fragment);
        var normalized = Normalize(fragment);
        foreach (var (label, isFootnote) in Labels(document))
        {
            if (inside.Contains((label, isFootnote)) || (isFootnote && !features.HasFlag(SyntaxFeature.Footnotes)))
            {
                continue;
            }

            var usage = isFootnote ? "[^" + label + "]" : "[" + label + "]";
            if (normalized.Contains(usage, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<(string Label, bool IsFootnote)> Labels(string text)
    {
        var result = new HashSet<(string, bool)>();
        foreach (Match match in DefinitionPattern().Matches(text))
        {
            var label = match.Groups["label"].Value;
            var isFootnote = label.StartsWith('^');
            result.Add((Normalize(isFootnote ? label[1..] : label), isFootnote));
        }

        return result;
    }

    // ラベルは大文字小文字と連続する空白を区別しない
    private static string Normalize(string text) => WhitespacePattern().Replace(text, " ").ToLowerInvariant();

    [GeneratedRegex(@"^ {0,3}\[(?<label>[^\]\r\n]+)\]:", RegexOptions.Multiline)]
    private static partial Regex DefinitionPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
