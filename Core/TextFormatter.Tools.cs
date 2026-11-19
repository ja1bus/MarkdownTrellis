using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace MarkdownTrellis.Core;

/// <summary>0.14.0で追加した書式の操作（チェックの切り替え、見出しのシフト、表の整形）。</summary>
public static partial class TextFormatter
{
    /// <summary>
    /// タスクリストの行の <c>[ ]</c> と <c>[x]</c>（<c>[X]</c>）を入れ替えた行を返す。タスクの行でなければ null。
    /// 変わるのは印の1文字だけ。
    /// </summary>
    public static string? ToggleTaskMark(string line)
    {
        var match = TaskMarkRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var mark = match.Groups[2];
        var replacement = mark.Value == " " ? "x" : " ";
        return line[..mark.Index] + replacement + line[(mark.Index + 1)..];
    }

    /// <summary>
    /// 見出しのレベルを1つ変える（delta が負なら上げる、正なら下げる）。選択がなければ文書全体、あれば選択した行が対象。
    /// コードブロックとFrontmatterは変えない。変わる見出しがなければ null。
    /// </summary>
    public static TextEdit? ShiftHeadings(string text, int selStart, int selEnd, int delta)
    {
        var positions = HeadingShiftPositions(text, [(selStart, selEnd)], delta);
        if (positions.Count == 0)
        {
            return null;
        }

        var lines = LineSplitter.Split(text);

        var lineOfFirst = lines.FindIndex(l => positions[0] >= l.Start && positions[0] <= l.ContentEnd);
        var lineOfLast = lines.FindIndex(l => positions[^1] >= l.Start && positions[^1] <= l.ContentEnd);
        var from = lines[lineOfFirst].Start;
        var to = lines[lineOfLast].ContentEnd;
        var builder = new StringBuilder(to - from + positions.Count);
        var cursor = from;
        foreach (var position in positions)
        {
            builder.Append(text, cursor, position - cursor);
            if (delta > 0)
            {
                builder.Append('#');
                cursor = position;
            }
            else
            {
                cursor = position + 1;
            }
        }

        builder.Append(text, cursor, to - cursor);

        // 削除・挿入した位置より後ろの位置だけをずらす（同じ文字の上に選択を保つ）
        int Map(int position) => position + (delta > 0 ? 1 : -1) * positions.Count(p => p < position);
        return new TextEdit(from, to - from, builder.ToString(), Map(selStart), Map(selEnd));
    }

    /// <summary>
    /// 複数の選択（複数選択・矩形選択）に対する見出しのシフト。空でない選択が1つでもあれば、それらが覆う行の和集合を、
    /// なければ文書全体を対象にする。各見出しの <c>#</c> を1文字だけ挿入・削除する編集を、前から順に返す（変わらなければ空）。
    /// 1文字ずつの編集にすることで、エディター側の選択位置がそのまま追従する。
    /// </summary>
    public static List<TextEdit> ShiftHeadingEdits(string text, IReadOnlyList<(int Start, int End)> selections, int delta) =>
        [.. HeadingShiftPositions(text, selections, delta).Select(p => delta > 0
            ? new TextEdit(p, 0, "#", p, p)
            : new TextEdit(p, 1, string.Empty, p, p))];

    /// <summary>レベルを変える見出しの、<c>#</c> の連続の先頭の位置（昇順）。</summary>
    private static List<int> HeadingShiftPositions(string text, IReadOnlyList<(int Start, int End)> selections, int delta)
    {
        var lines = LineSplitter.Split(text);
        var targets = new bool[lines.Count];
        var ranges = selections.Where(s => s.Start != s.End).ToList();
        if (ranges.Count == 0)
        {
            Array.Fill(targets, true);
        }
        else
        {
            foreach (var (start, end) in ranges)
            {
                var (_, _, covered) = CoveredLines(text, start, end);
                var first = lines.FindIndex(l => l.Start == covered[0].Start);
                for (var i = first; i < first + covered.Count; i++)
                {
                    targets[i] = true;
                }
            }
        }

        // 見出しは解析結果で決める。最上位（引用・リストの外）のATX見出しだけを対象にし、コード・HTML・Frontmatterの中の # は変えない
        var headings = new HashSet<int>();
        var document = Markdown.Parse(text, MarkdownRenderer.GetPipeline(SyntaxFeature.None));
        foreach (var heading in document.OfType<HeadingBlock>())
        {
            if (!heading.IsSetext)
            {
                headings.Add(heading.Line);
            }
        }

        var excluded = ExcludedLines(lines);
        var positions = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!targets[i] || excluded[i] || !headings.Contains(i))
            {
                continue;
            }

            var match = HeadingRegex().Match(lines[i].Content);
            if (!match.Success)
            {
                continue;
            }

            var level = match.Groups[1].Length;
            if ((delta < 0 && level <= 1) || (delta > 0 && level >= 6))
            {
                continue;
            }

            positions.Add(lines[i].Start + match.Groups[1].Index);
        }

        return positions;
    }

    /// <summary>
    /// カーソルを含むパイプテーブルの列幅をそろえる。表の外なら null。整っていれば長さ0の空の編集を返す。
    /// </summary>
    public static TextEdit? FormatTable(string text, int selStart, int selEnd, string eol)
    {
        var caret = Math.Min(selStart, selEnd);
        var lines = LineSplitter.Split(text);
        var cursorLine = lines.FindIndex(l => caret >= l.Start && caret <= l.ContentEnd);
        if (cursorLine < 0)
        {
            return null;
        }

        var excluded = ExcludedLines(lines, text);
        var rows = new TableRow?[lines.Count];
        bool IsRow(int i)
        {
            if (excluded[i] || string.IsNullOrWhiteSpace(lines[i].Content) || lines[i].Content.TrimStart().StartsWith('>'))
            {
                return false;
            }

            rows[i] ??= TableRow.Parse(lines[i].Content);
            return rows[i] is not null;
        }

        if (!IsRow(cursorLine))
        {
            return null;
        }

        var top = cursorLine;
        while (top > 0 && IsRow(top - 1))
        {
            top--;
        }

        var bottom = cursorLine;
        while (bottom < lines.Count - 1 && IsRow(bottom + 1))
        {
            bottom++;
        }

        var delimiter = -1;
        for (var i = top + 1; i <= bottom; i++)
        {
            if (rows[i]!.IsDelimiter)
            {
                delimiter = i;
                break;
            }
        }

        var header = delimiter - 1;
        if (delimiter < 0 || header > cursorLine || rows[header]!.Cells.Count != rows[delimiter]!.Cells.Count)
        {
            return null;
        }

        var columns = rows[header]!.Cells.Count;
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            widths[c] = 3;
            for (var i = header; i <= bottom; i++)
            {
                if (i != delimiter && c < rows[i]!.Cells.Count)
                {
                    widths[c] = Math.Max(widths[c], DisplayWidth.Of(rows[i]!.Cells[c].Text));
                }
            }
        }

        var builder = new StringBuilder();
        var newCaret = -1;
        var caretCell = rows[cursorLine]!.CellAt(caret - lines[cursorLine].Start);
        for (var i = header; i <= bottom; i++)
        {
            if (i > header)
            {
                builder.Append(eol);
            }

            var row = rows[i]!;
            builder.Append(row.Indent).Append('|');
            var count = Math.Max(columns, row.Cells.Count);
            for (var c = 0; c < count; c++)
            {
                builder.Append(' ');
                if (i == cursorLine && c == Math.Min(caretCell, count - 1))
                {
                    newCaret = builder.Length;
                }

                if (c >= columns)
                {
                    builder.Append(row.Cells[c].Text);
                }
                else if (i == delimiter)
                {
                    builder.Append(Delimiter(row.Cells[c].Text, widths[c]));
                }
                else
                {
                    var cell = c < row.Cells.Count ? row.Cells[c].Text : string.Empty;
                    builder.Append(cell).Append(' ', widths[c] - DisplayWidth.Of(cell));
                }

                builder.Append(" |");
            }
        }

        var from = lines[header].Start;
        var to = lines[bottom].ContentEnd;
        var replacement = builder.ToString();
        if (text.AsSpan(from, to - from).SequenceEqual(replacement))
        {
            return new TextEdit(selStart, 0, string.Empty, selStart, selEnd);
        }

        var position = from + newCaret;
        return new TextEdit(from, to - from, replacement, position, position);
    }

    private static string Delimiter(string cell, int width)
    {
        var left = cell.StartsWith(':');
        var right = cell.Length > 1 && cell.EndsWith(':');
        var dashes = width - (left ? 1 : 0) - (right ? 1 : 0);
        return (left ? ":" : string.Empty) + new string('-', dashes) + (right ? ":" : string.Empty);
    }

    /// <summary>
    /// コードブロック（閉じていないものは文書の終わりまで）とFrontmatterの行。text を渡すと、解析結果のコードブロック
    /// （字下げのコード、リスト・引用の中のフェンスを含む）の行も加える。
    /// </summary>
    private static bool[] ExcludedLines(List<SourceLine> lines, string? text = null)
    {
        var excluded = FenceAndFrontmatterLines(lines);
        if (text is not null)
        {
            var document = Markdown.Parse(text, MarkdownRenderer.GetPipeline(SyntaxFeature.None));
            foreach (var code in document.Descendants<CodeBlock>())
            {
                var last = Math.Min(code.Line + Math.Max(code.Lines.Count, 1) - 1, lines.Count - 1);
                if (code is FencedCodeBlock)
                {
                    // 閉じのフェンスの行は Lines に含まれない
                    last = Math.Min(Math.Max(last, LineOf(lines, code.Span.End)), lines.Count - 1);
                }

                for (var i = code.Line; i <= last && i >= 0; i++)
                {
                    excluded[i] = true;
                }
            }
        }

        return excluded;
    }

    private static int LineOf(List<SourceLine> lines, int position)
    {
        var index = lines.FindIndex(l => position >= l.Start && position < Math.Max(l.End, l.Start + 1));
        return index < 0 ? lines.Count - 1 : index;
    }

    private static bool[] FenceAndFrontmatterLines(List<SourceLine> lines)
    {
        var excluded = new bool[lines.Count];
        if (lines.Count > 0 && lines[0].Content.TrimEnd() == "---")
        {
            var close = lines.FindIndex(1, l => l.Content.TrimEnd() == "---");
            for (var i = 0; i <= close; i++)
            {
                excluded[i] = true;
            }
        }

        foreach (var block in LineSplitter.FindCodeBlocks(lines))
        {
            var close = block.Close < 0 ? lines.Count - 1 : block.Close;
            for (var i = block.Open; i <= close; i++)
            {
                excluded[i] = true;
            }
        }

        return excluded;
    }

    private sealed record TableCell(string Text, int RawStart);

    /// <summary>表の1行。セルは前後の空白を除いた文字と、元の行での開始位置。</summary>
    private sealed class TableRow
    {
        public string Indent { get; private init; } = string.Empty;

        public List<TableCell> Cells { get; } = [];

        public bool IsDelimiter => Cells.Count > 0 && Cells.TrueForAll(c => DelimiterCellRegex().IsMatch(c.Text));

        /// <summary>行内の位置が何番目のセルにあるか。</summary>
        public int CellAt(int column)
        {
            var index = 0;
            for (var i = 0; i < Cells.Count; i++)
            {
                if (Cells[i].RawStart <= column)
                {
                    index = i;
                }
            }

            return index;
        }

        /// <summary><c>|</c> で区切られた行を分ける。<c>\|</c> とコードスパンの中では分けない。区切りがなければ null。</summary>
        public static TableRow? Parse(string content)
        {
            var indentLength = content.Length - content.TrimStart().Length;
            var end = content.TrimEnd().Length;
            var row = new TableRow { Indent = content[..indentLength] };
            var i = indentLength;
            var hasPipe = false;
            if (i < end && content[i] == '|')
            {
                hasPipe = true;
                i++;
            }

            var cellStart = i;
            var cell = new StringBuilder();
            var endsWithPipe = false;
            while (i < end)
            {
                var c = content[i];
                if (c == '\\' && i + 1 < end)
                {
                    cell.Append(c).Append(content[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    var run = 0;
                    while (i + run < end && content[i + run] == '`')
                    {
                        run++;
                    }

                    var close = FindClosingRun(content, i + run, end, run);
                    var stop = close < 0 ? i + run : close + run;
                    cell.Append(content, i, stop - i);
                    i = stop;
                    continue;
                }

                if (c == '|')
                {
                    hasPipe = true;
                    row.Cells.Add(new TableCell(cell.ToString().Trim(), cellStart));
                    cell.Clear();
                    i++;
                    cellStart = i;
                    endsWithPipe = i >= end;
                    continue;
                }

                cell.Append(c);
                i++;
            }

            if (!hasPipe)
            {
                return null;
            }

            if (!endsWithPipe)
            {
                row.Cells.Add(new TableCell(cell.ToString().Trim(), cellStart));
            }

            return row.Cells.Count == 0 ? null : row;
        }

        private static int FindClosingRun(string content, int from, int end, int run)
        {
            var i = from;
            while (i < end)
            {
                if (content[i] != '`')
                {
                    i++;
                    continue;
                }

                var length = 0;
                while (i + length < end && content[i + length] == '`')
                {
                    length++;
                }

                if (length == run)
                {
                    return i;
                }

                i += length;
            }

            return -1;
        }
    }

    [GeneratedRegex(@"^(\s*(?:[-*+]|\d{1,9}[.)])[ \t]+\[)( |x|X)(\])")]
    private static partial Regex TaskMarkRegex();

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex DelimiterCellRegex();
}

/// <summary>等幅フォントでの表示幅。East Asian WidthのFullwidth・Wideを2、それ以外を1とする（概算の範囲表）。</summary>
public static class DisplayWidth
{
    private static readonly (int From, int To)[] Wide =
    [
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
        (0x2E80, 0x303E), (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F),
        (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6),
        (0x16FE0, 0x16FE4), (0x17000, 0x18AFF), (0x1B000, 0x1B2FF), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF),
        (0x1F18E, 0x1F18E), (0x1F191, 0x1F19A), (0x1F200, 0x1F251), (0x1F300, 0x1F64F), (0x1F680, 0x1F6FF),
        (0x1F7E0, 0x1F7EB), (0x1F90C, 0x1F9FF), (0x1FA70, 0x1FAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    ];

    public static int Of(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += IsWide(rune.Value) ? 2 : 1;
        }

        return width;
    }

    public static bool IsWide(int codePoint)
    {
        var low = 0;
        var high = Wide.Length - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (codePoint < Wide[mid].From)
            {
                high = mid - 1;
            }
            else if (codePoint > Wide[mid].To)
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
