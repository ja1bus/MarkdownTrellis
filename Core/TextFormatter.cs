using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownTrellis.Core;

public enum InlineStyle { Bold, Italic, Strikethrough, Code }

public enum LineStyle { BulletList, NumberedList, TaskList, Quote }

/// <summary>文書の一部の置換と、置換後の選択範囲。位置はUTF-16の文字位置。</summary>
/// <summary>選択範囲と、それが含まれる行。</summary>
public readonly record struct SelectionSpan(int Start, int End, int FirstLine, int LastLine);

public sealed record TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionEnd, bool EmphasisIneffective = false);

/// <summary>書式ボタンの変換。Notepad++に依存しない純粋な処理とする。</summary>
public static partial class TextFormatter
{
    /// <summary>
    /// 行単位の操作用に、行が重なる選択を1つにまとめる。後ろの選択から順に返す（適用しても前の位置がずれない）。
    /// </summary>
    public static List<SelectionSpan> OnePerLine(IReadOnlyList<SelectionSpan> selections)
    {
        var kept = new List<SelectionSpan>();
        foreach (var selection in selections.OrderByDescending(s => s.Start))
        {
            if (kept.Count == 0 || selection.LastLine < kept[^1].FirstLine)
            {
                kept.Add(selection);
            }
        }

        return kept;
    }

    public static TextEdit ToggleInline(string text, int selStart, int selEnd, InlineStyle style)
    {
        var (start, end) = Order(selStart, selEnd);
        var marker = Marker(style);
        if (start == end)
        {
            return ToggleEmpty(text, start, marker);
        }

        var selected = text[start..end];
        if (selected.Contains('\n') || selected.Contains('\r'))
        {
            return ToggleLines(text, start, end, style);
        }

        return ToggleSegment(text, start, end, style);
    }

    private static TextEdit ToggleEmpty(string text, int pos, string marker)
    {
        if (pos >= marker.Length && pos + marker.Length <= text.Length
            && string.CompareOrdinal(text, pos - marker.Length, marker, 0, marker.Length) == 0
            && string.CompareOrdinal(text, pos, marker, 0, marker.Length) == 0)
        {
            var at = pos - marker.Length;
            return new TextEdit(at, marker.Length * 2, string.Empty, at, at);
        }

        return new TextEdit(pos, 0, marker + marker, pos + marker.Length, pos + marker.Length);
    }

    private static TextEdit ToggleLines(string text, int start, int end, InlineStyle style)
    {
        var selected = text[start..end];
        var builder = new StringBuilder(selected.Length + 32);
        var lines = LineSplitter.Split(selected);
        var removable = lines.Where(l => !string.IsNullOrWhiteSpace(l.Content)).ToList();
        var removeAll = removable.Count > 0 && removable.All(l => HasInnerMarker(l.Content.Trim(), style));
        var ineffective = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Content))
            {
                builder.Append(line.Content).Append(line.Eol);
                continue;
            }

            builder.Append(WrapLine(line.Content, style, removeAll)).Append(line.Eol);
            if (!removeAll && style != InlineStyle.Code)
            {
                var coreStart = start + line.Start + (line.Content.Length - line.Content.TrimStart().Length);
                var core = line.Content.Trim();
                var marker = Marker(style);
                ineffective |= !IsEffective(text, coreStart, coreStart + core.Length, marker + core + marker);
            }
        }

        var replacement = builder.ToString();
        return new TextEdit(start, end - start, replacement, start, start + replacement.Length, ineffective);
    }

    private static string WrapLine(string line, InlineStyle style, bool remove)
    {
        var marker = Marker(style);
        var leading = line.Length - line.TrimStart().Length;
        var core = line.Trim();
        var trailing = line.Length - line.TrimEnd().Length;
        var updated = remove ? core[marker.Length..^marker.Length] : marker + core + marker;
        return line[..leading] + updated + (trailing > 0 ? line[^trailing..] : string.Empty);
    }

    private static TextEdit ToggleSegment(string text, int start, int end, InlineStyle style)
    {
        // 選択の前後の空白は記号の外に残す
        var coreStart = start;
        var coreEnd = end;
        while (coreStart < coreEnd && char.IsWhiteSpace(text[coreStart]))
        {
            coreStart++;
        }

        while (coreEnd > coreStart && char.IsWhiteSpace(text[coreEnd - 1]))
        {
            coreEnd--;
        }

        if (coreStart == coreEnd)
        {
            return new TextEdit(start, 0, string.Empty, start, end);
        }

        if (style == InlineStyle.Code && TryUnwrapLongCode(text, coreStart, coreEnd) is { } unwrapped)
        {
            return unwrapped;
        }

        var marker = Marker(style);
        if (HasOuterMarker(text, coreStart, coreEnd, style))
        {
            var core = text[coreStart..coreEnd];
            var newStart = coreStart - marker.Length;
            return new TextEdit(newStart, core.Length + marker.Length * 2, core, newStart, newStart + core.Length);
        }

        var selection = text[coreStart..coreEnd];
        if (HasInnerMarker(selection, style))
        {
            var inner = selection[marker.Length..^marker.Length];
            return new TextEdit(coreStart, selection.Length, inner, coreStart, coreStart + inner.Length);
        }

        if (style == InlineStyle.Code && selection.Contains('`'))
        {
            var run = new string('`', LongestRun(selection, '`') + 1);
            var wrapped = run + " " + selection + " " + run;
            return new TextEdit(coreStart, selection.Length, wrapped, coreStart + run.Length + 1, coreStart + run.Length + 1 + selection.Length);
        }

        var replacement = marker + selection + marker;
        var ineffective = style != InlineStyle.Code && !IsEffective(text, coreStart, coreEnd, replacement);
        return new TextEdit(coreStart, selection.Length, replacement, coreStart + marker.Length, coreStart + marker.Length + selection.Length, ineffective);
    }

    /// <summary>2個以上のバッククォートと内側の空白で囲まれたコードを解除する（囲みの内側か、囲み全体を選択した場合）。</summary>
    private static TextEdit? TryUnwrapLongCode(string text, int start, int end)
    {
        // 囲み全体が選択されている場合
        var selection = text[start..end];
        var run = 0;
        while (run < selection.Length && selection[run] == '`')
        {
            run++;
        }

        if (run >= 2 && selection.Length > run * 2 && selection.EndsWith(new string('`', run), StringComparison.Ordinal))
        {
            var inner = selection[run..^run];
            if (inner.Length >= 3 && inner[0] == ' ' && inner[^1] == ' ')
            {
                inner = inner[1..^1];
            }

            return new TextEdit(start, selection.Length, inner, start, start + inner.Length);
        }

        // 囲みの内側が選択されている場合
        var padded = start > 0 && end < text.Length && text[start - 1] == ' ' && text[end] == ' ';
        var left = start - (padded ? 1 : 0);
        var right = end + (padded ? 1 : 0);
        var leftRun = 0;
        while (left - leftRun > 0 && text[left - leftRun - 1] == '`')
        {
            leftRun++;
        }

        var rightRun = 0;
        while (right + rightRun < text.Length && text[right + rightRun] == '`')
        {
            rightRun++;
        }

        if (leftRun < 2 || leftRun != rightRun)
        {
            return null;
        }

        var from = left - leftRun;
        var content = text[start..end];
        return new TextEdit(from, right + rightRun - from, content, from, from + content.Length);
    }

    /// <summary>記号を入れた行を解析し、強調が増えたか確認する（<c>**「重要」**です</c> のように効かない場合を検出する）。</summary>
    private static bool IsEffective(string text, int coreStart, int coreEnd, string replacement)
    {
        var lineStart = coreStart == 0 ? 0 : text.LastIndexOfAny(['\r', '\n'], coreStart - 1) + 1;
        var lineEnd = text.IndexOfAny(['\r', '\n'], coreEnd);
        lineEnd = lineEnd < 0 ? text.Length : lineEnd;
        var before = text[lineStart..lineEnd];
        var after = text[lineStart..coreStart] + replacement + text[coreEnd..lineEnd];
        var pipeline = MarkdownRenderer.GetPipeline(SyntaxFeature.None);
        return CountEmphasis(after, pipeline) > CountEmphasis(before, pipeline);
    }

    private static int CountEmphasis(string line, MarkdownPipeline pipeline) =>
        Markdown.Parse(line, pipeline).Descendants<EmphasisInline>().Count();

    private static bool HasOuterMarker(string text, int start, int end, InlineStyle style)
    {
        var c = Marker(style)[0];
        var left = 0;
        for (var i = start - 1; i >= 0 && text[i] == c; i--)
        {
            left++;
        }

        var right = 0;
        for (var i = end; i < text.Length && text[i] == c; i++)
        {
            right++;
        }

        return RunsMatch(Math.Min(left, right), style);
    }

    private static bool HasInnerMarker(string selection, InlineStyle style)
    {
        var marker = Marker(style);
        if (selection.Length <= marker.Length * 2)
        {
            return false;
        }

        var c = marker[0];
        var left = 0;
        while (left < selection.Length && selection[left] == c)
        {
            left++;
        }

        var right = 0;
        while (right < selection.Length && selection[selection.Length - 1 - right] == c)
        {
            right++;
        }

        return RunsMatch(Math.Min(left, right), style);
    }

    private static bool RunsMatch(int run, InlineStyle style) => style switch
    {
        InlineStyle.Bold => run >= 2,
        InlineStyle.Italic => run % 2 == 1,
        InlineStyle.Strikethrough => run >= 2,
        _ => run == 1,
    };

    public static TextEdit SetHeading(string text, int selStart, int selEnd, int level)
    {
        var (start, end, lines) = CoveredLines(text, selStart, selEnd);
        var targets = lines.Where(l => !string.IsNullOrWhiteSpace(l.Content) || lines.Count == 1).ToList();
        var removeAll = level == 0 || targets.All(l => HeadingLevel(l.Content) == level);
        var replacement = RewriteLines(lines, line =>
        {
            if (string.IsNullOrWhiteSpace(line) && lines.Count > 1)
            {
                return line;
            }

            var body = HeadingRegex().Replace(line, string.Empty);
            return removeAll ? body : new string('#', level) + " " + body;
        });
        return Finish(text, selStart, selEnd, start, end, replacement);
    }

    public static TextEdit ToggleLineStyle(string text, int selStart, int selEnd, LineStyle style)
    {
        var (start, end, lines) = CoveredLines(text, selStart, selEnd);
        var targets = lines.Where(l => !string.IsNullOrWhiteSpace(l.Content) || lines.Count == 1).ToList();
        var removeAll = targets.Count > 0 && targets.All(l => HasLineStyle(l.Content, style));
        var number = style == LineStyle.NumberedList && !removeAll ? PreviousNumber(text, start, lines[0].Content) : 0;
        var replacement = RewriteLines(lines, line =>
        {
            if (string.IsNullOrWhiteSpace(line) && lines.Count > 1)
            {
                return style == LineStyle.Quote && !removeAll ? ">" : line;
            }

            if (style == LineStyle.Quote)
            {
                return removeAll ? QuoteRegex().Replace(line, string.Empty, 1) : "> " + line;
            }

            var match = ListMarkerRegex().Match(line);
            var indent = match.Success ? match.Groups[1].Value : LeadingSpaces(line);
            var body = match.Success ? line[match.Length..] : line.TrimStart(' ', '\t');
            if (removeAll)
            {
                return indent + body;
            }

            number++;
            return indent + style switch
            {
                LineStyle.BulletList => "- ",
                LineStyle.NumberedList => number + ". ",
                _ => "- [ ] ",
            } + body;
        });
        return Finish(text, selStart, selEnd, start, end, replacement);
    }

    /// <summary>すぐ上の行が同じ字下げの番号付きリストなら、その番号を返す（続きから番号を振るため）。</summary>
    private static int PreviousNumber(string text, int lineStart, string firstLine)
    {
        if (lineStart == 0)
        {
            return 0;
        }

        var lines = LineSplitter.Split(text[..lineStart]);
        var match = lines.Count >= 2 ? PreviousNumberRegex().Match(lines[^2].Content) : Match.Empty;
        var indent = ListMarkerRegex().Match(firstLine) is { Success: true } marker ? marker.Groups[1].Value : LeadingSpaces(firstLine);
        return match.Success && match.Groups[1].Value == indent ? int.Parse(match.Groups[2].Value) : 0;
    }

    /// <summary>
    /// カーソルがコードブロック内にあるときの操作。閉じていなければ閉じ、閉じていれば囲みを解除する。
    /// ブロックの外なら null を返す。
    /// </summary>
    public static TextEdit? ToggleCodeFence(string text, int selStart, int selEnd, string eol)
    {
        var (start, end) = Order(selStart, selEnd);
        var lines = LineSplitter.Split(text);
        var first = Math.Max(0, lines.FindIndex(l => start >= l.Start && start <= l.ContentEnd));
        var last = Math.Max(first, lines.FindIndex(l => end >= l.Start && end <= l.ContentEnd));
        foreach (var block in LineSplitter.FindCodeBlocks(lines))
        {
            if (first < block.Open || (block.Close >= 0 && first > block.Close))
            {
                continue;
            }

            if (block.Close >= 0)
            {
                var inner = text[lines[block.Open].End..lines[block.Close].Start];
                var from = lines[block.Open].Start;
                return new TextEdit(from, lines[block.Close].End - from, inner, from, from + inner.Length);
            }

            var fence = block.Fence;
            var line = lines[last];
            if (last > block.Open && string.IsNullOrWhiteSpace(line.Content))
            {
                return new TextEdit(line.Start, line.ContentEnd - line.Start, fence, line.Start + fence.Length, line.Start + fence.Length);
            }

            var insert = eol + fence;
            var caret = line.ContentEnd + insert.Length;
            return new TextEdit(line.ContentEnd, 0, insert, caret, caret);
        }

        return null;
    }

    public static TextEdit InsertTable(string text, int selStart, int selEnd, int columns, int rows, string headerFormat, string eol)
    {
        columns = Math.Clamp(columns, 1, 50);
        rows = Math.Clamp(rows, 0, 500);
        var builder = new StringBuilder();
        var firstHeader = string.Format(headerFormat, 1);
        builder.Append('|');
        for (var c = 1; c <= columns; c++)
        {
            builder.Append(' ').Append(string.Format(headerFormat, c)).Append(" |");
        }

        builder.Append(eol).Append('|');
        for (var c = 0; c < columns; c++)
        {
            builder.Append(" --- |");
        }

        for (var r = 0; r < rows; r++)
        {
            builder.Append(eol).Append('|');
            for (var c = 0; c < columns; c++)
            {
                builder.Append("     |");
            }
        }

        return InsertBlock(text, selStart, selEnd, builder.ToString(), eol, 2, firstHeader.Length);
    }

    public static TextEdit InsertHorizontalRule(string text, int selStart, int selEnd, string eol) =>
        InsertBlock(text, selStart, selEnd, "---", eol, 3, 0);

    public static TextEdit InsertLink(string text, int selStart, int selEnd, string label, string url, bool isImage)
    {
        var (start, end) = Order(selStart, selEnd);
        var target = url.Any(char.IsWhiteSpace) ? "<" + url + ">" : url;
        var escaped = label.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
        var replacement = (isImage ? "!" : string.Empty) + "[" + escaped + "](" + target + ")";
        return new TextEdit(start, end - start, replacement, start + replacement.Length, start + replacement.Length);
    }

    public static TextEdit InsertCodeBlock(string text, int selStart, int selEnd, string language, string eol)
    {
        var (start, end) = Order(selStart, selEnd);
        var selected = text[start..end];
        var fence = new string('`', Math.Max(3, LongestRun(selected, '`') + 1));
        var opening = fence + language.Trim();
        var body = selected.EndsWith('\n') || selected.EndsWith('\r') ? selected : selected + eol;
        var block = opening + eol + body + fence;
        var caret = opening.Length + eol.Length;
        var edit = InsertBlock(text, start, end, block, eol, caret, selected.Length == 0 ? 0 : selected.Length);
        return edit;
    }

    public static string FindEol(int scintillaEolMode) => scintillaEolMode switch
    {
        1 => "\r",
        2 => "\n",
        _ => "\r\n",
    };

    private static TextEdit InsertBlock(string text, int selStart, int selEnd, string block, string eol, int selectFrom, int selectLength)
    {
        var (start, end) = Order(selStart, selEnd);
        var before = text[..start];
        var after = text[end..];
        var prefix = string.Empty;
        if (before.Length > 0)
        {
            if (!EndsWithEol(before))
            {
                prefix = eol + eol;
            }
            else if (!EndsWithBlankLine(before))
            {
                prefix = eol;
            }
        }

        var suffix = string.Empty;
        if (after.Length > 0)
        {
            if (!StartsWithEol(after))
            {
                suffix = eol + eol;
            }
            else if (!StartsWithBlankLine(after))
            {
                suffix = eol;
            }
        }
        else
        {
            suffix = eol;
        }

        var replacement = prefix + block + suffix;
        var select = start + prefix.Length + selectFrom;
        return new TextEdit(start, end - start, replacement, select, select + selectLength);
    }

    private static bool EndsWithEol(string s) => s[^1] is '\n' or '\r';

    private static bool StartsWithEol(string s) => s[0] is '\n' or '\r';

    private static bool EndsWithBlankLine(string s)
    {
        var lines = LineSplitter.Split(s);
        return lines.Count < 2 || string.IsNullOrWhiteSpace(lines[^2].Content);
    }

    private static bool StartsWithBlankLine(string s)
    {
        var lines = LineSplitter.Split(s);
        return lines.Count < 2 || string.IsNullOrWhiteSpace(lines[1].Content);
    }

    private static (int Start, int End, List<SourceLine> Lines) CoveredLines(string text, int selStart, int selEnd)
    {
        var (start, end) = Order(selStart, selEnd);
        var all = LineSplitter.Split(text);
        var first = all.FindIndex(l => start >= l.Start && start <= l.ContentEnd);
        first = first < 0 ? 0 : first;
        var last = all.FindIndex(l => end >= l.Start && (end <= l.ContentEnd || end < l.End));

        // 選択の終わりが行頭なら、その行は対象にしない（改行の種類によらない）
        if (end > start && last > first && all[last].Start == end)
        {
            last--;
        }

        last = last < first ? first : last;
        var lines = all.GetRange(first, last - first + 1);
        return (lines[0].Start, lines[^1].ContentEnd, lines);
    }

    private static string RewriteLines(List<SourceLine> lines, Func<string, string> rewrite)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(rewrite(lines[i].Content));
            if (i < lines.Count - 1)
            {
                builder.Append(lines[i].Eol);
            }
        }

        return builder.ToString();
    }

    private static TextEdit Finish(string text, int selStart, int selEnd, int start, int end, string replacement)
    {
        if (selStart == selEnd)
        {
            var offset = selStart + (replacement.Length - (end - start));
            var caret = Math.Clamp(offset, start, start + replacement.Length);
            return new TextEdit(start, end - start, replacement, caret, caret);
        }

        return new TextEdit(start, end - start, replacement, start, start + replacement.Length);
    }

    private static bool HasLineStyle(string line, LineStyle style) => style switch
    {
        LineStyle.Quote => QuoteRegex().IsMatch(line),
        LineStyle.TaskList => TaskRegex().IsMatch(line),
        LineStyle.NumberedList => NumberRegex().IsMatch(line),
        _ => BulletRegex().IsMatch(line) && !TaskRegex().IsMatch(line),
    };

    private static int HeadingLevel(string line)
    {
        var match = HeadingRegex().Match(line);
        return match.Success ? match.Groups[1].Length : 0;
    }

    private static string LeadingSpaces(string line) => line[..(line.Length - line.TrimStart(' ', '\t').Length)];

    private static string Marker(InlineStyle style) => style switch
    {
        InlineStyle.Bold => "**",
        InlineStyle.Italic => "*",
        InlineStyle.Strikethrough => "~~",
        _ => "`",
    };

    private static (int, int) Order(int a, int b) => a <= b ? (a, b) : (b, a);

    private static int LongestRun(string text, char c)
    {
        var longest = 0;
        var current = 0;
        foreach (var ch in text)
        {
            current = ch == c ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+|$)")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^([ \t]*)(?:[-*+] \[[ xX]\] |[-*+] |\d+[.)] )")]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"^[ \t]*[-*+] \[[ xX]\] ")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"^[ \t]*\d+[.)] ")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^([ \t]*)(\d{1,9})[.)] ")]
    private static partial Regex PreviousNumberRegex();

    [GeneratedRegex(@"^[ \t]*[-*+] ")]
    private static partial Regex BulletRegex();
}
