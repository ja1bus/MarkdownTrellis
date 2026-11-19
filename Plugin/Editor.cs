using System.Runtime.InteropServices;
using System.Text;
using MarkdownTrellis.Core;

namespace MarkdownTrellis.Plugin;

/// <summary>現在のScintillaへのアクセス。位置はScintillaのバイト位置と文字位置を変換して扱う。</summary>
internal static unsafe class Editor
{
    public static nint Current
    {
        get
        {
            var which = 0;
            Native.SendMessage(PluginHost.Npp.Npp, Native.NppmGetCurrentScintilla, 0, (nint)(&which));
            return which == 0 ? PluginHost.Npp.Scintilla1 : PluginHost.Npp.Scintilla2;
        }
    }

    public static nint Send(uint message, nint wParam = 0, nint lParam = 0) => Native.SendMessage(Current, message, wParam, lParam);

    public static Encoding CurrentEncoding
    {
        get
        {
            var codePage = (int)Send(Native.SciGetCodePage);
            return codePage switch
            {
                65001 => new UTF8Encoding(false),
                0 => Encoding.GetEncoding((int)Native.GetACP()),
                _ => Encoding.GetEncoding(codePage),
            };
        }
    }

    public static string Eol => TextFormatter.FindEol((int)Send(Native.SciGetEolMode));

    public static bool IsReadOnly => Send(Native.SciGetReadOnly) != 0;

    public static byte[] GetBytes()
    {
        var length = (int)Send(Native.SciGetLength);
        var pointer = Send(Native.SciGetCharacterPointer);
        var bytes = new byte[length];
        if (length > 0 && pointer != 0)
        {
            Marshal.Copy(pointer, bytes, 0, length);
        }

        return bytes;
    }

    public static int GetLengthInBytes() => (int)Send(Native.SciGetLength);

    public static string GetText() => CurrentEncoding.GetString(GetBytes());

    public static int CurrentLine => (int)Send(Native.SciLineFromPosition, Send(Native.SciGetCurrentPos));

    /// <summary>原文を変更せず、指定行の先頭に移動する。grabFocus なら入力フォーカスもNotepad++へ戻す。</summary>
    public static void GoToLine(int line, bool grabFocus = true)
    {
        if (line < 0 || line >= Send(Native.SciGetLineCount)) return;
        Send(Native.SciEnsureVisible, line);
        Send(Native.SciGotoLine, line);
        if (grabFocus)
        {
            Send(Native.SciGrabFocus);
        }
    }

    /// <summary>
    /// バイト列を厳密に文字列へ戻せて、符号化し直すと同じバイト列になるか。文字コードとして読めないバイト（U+FFFDに置き換わる）があると
    /// 文字位置とバイト位置の変換がずれ、置き換えた範囲が化けるので、書き換えの前に確かめる。
    /// </summary>
    public static bool RoundTrips(byte[] bytes, Encoding encoding) => EncodingCheck.RoundTrips(bytes, encoding);

    public static bool IsEncodingValid => RoundTrips(GetBytes(), CurrentEncoding);

    /// <summary>
    /// 指定行のタスクの印（<c>[ ]</c> と <c>[x]</c>）を1バイトだけ切り替える（1回のUndo）。カーソル・選択・フォーカスは動かさない。
    /// 結果: 0=切り替えた、1=タスクの行でない、2=文字コードとして読めない。
    /// </summary>
    public static int ToggleTask(int line)
    {
        var encoding = CurrentEncoding;
        var start = (int)Send(Native.SciPositionFromLine, line);
        var end = (int)Send(Native.SciGetLineEndPosition, line);
        var bytes = GetBytes();
        if (start < 0 || end < start || end > bytes.Length)
        {
            return 1;
        }

        // 他の書式操作と同じく、文書のどこかに読めないバイトがあれば中止する
        if (!RoundTrips(bytes, encoding))
        {
            return 2;
        }

        var lineBytes = bytes.AsSpan(start, end - start);

        var text = encoding.GetString(lineBytes);
        if (TextFormatter.ToggleTaskMark(text) is not { } toggled)
        {
            return 1;
        }

        var index = 0;
        while (index < text.Length && text[index] == toggled[index])
        {
            index++;
        }

        var position = start + encoding.GetByteCount(text.AsSpan(0, index));
        var selection = SelectionState.Capture();
        Send(Native.SciBeginUndoAction);
        try
        {
            Replace(position, position + 1, encoding.GetBytes(toggled.AsSpan(index, 1).ToArray()));
        }
        finally
        {
            Send(Native.SciEndUndoAction);
        }

        // 同じ長さの置換でも、直後にあった端点は前へ寄るので、すべての選択を元に戻す（SCI_SETSELはスクロールするので使わない）
        selection.Restore();
        return 0;
    }

    /// <summary>
    /// 全選択（複数選択・矩形選択を含む）の端点・仮想空白・主選択。位置の変わらない置換の後に、スクロールとフォーカスを動かさずに戻す。
    /// </summary>
    private sealed class SelectionState
    {
        private int _mode;
        private nint _main;
        private (nint Anchor, nint Caret, nint AnchorSpace, nint CaretSpace)[] _ranges = [];
        private (nint Anchor, nint Caret, nint AnchorSpace, nint CaretSpace) _rectangle;

        private bool IsRectangular => _mode is Native.ScSelRectangle or Native.ScSelThin;

        public static SelectionState Capture()
        {
            var state = new SelectionState { _mode = (int)Send(Native.SciGetSelectionMode), _main = Send(Native.SciGetMainSelection) };
            if (state.IsRectangular)
            {
                state._rectangle = ReadRectangle();
                return state;
            }

            var count = (int)Send(Native.SciGetSelections);
            state._ranges = new (nint, nint, nint, nint)[count];
            for (var i = 0; i < count; i++)
            {
                state._ranges[i] = ReadRange(i);
            }

            return state;
        }

        public void Restore()
        {
            if (IsRectangular)
            {
                if (ReadRectangle() != _rectangle)
                {
                    Send(Native.SciSetRectangularSelectionAnchor, _rectangle.Anchor);
                    Send(Native.SciSetRectangularSelectionCaret, _rectangle.Caret);
                    Send(Native.SciSetRectangularSelectionAnchorVirtualSpace, _rectangle.AnchorSpace);
                    Send(Native.SciSetRectangularSelectionCaretVirtualSpace, _rectangle.CaretSpace);
                }

                return;
            }

            if (Send(Native.SciGetSelections) != _ranges.Length)
            {
                return;
            }

            for (var i = 0; i < _ranges.Length; i++)
            {
                var saved = _ranges[i];
                if (ReadRange(i) == saved)
                {
                    continue;
                }

                Send(Native.SciSetSelectionNAnchor, i, saved.Anchor);
                Send(Native.SciSetSelectionNCaret, i, saved.Caret);
                Send(Native.SciSetSelectionNAnchorVirtualSpace, i, saved.AnchorSpace);
                Send(Native.SciSetSelectionNCaretVirtualSpace, i, saved.CaretSpace);
            }

            if (Send(Native.SciGetMainSelection) != _main)
            {
                Send(Native.SciSetMainSelection, _main);
            }
        }

        private static (nint, nint, nint, nint) ReadRange(int i) => (
            Send(Native.SciGetSelectionNAnchor, i),
            Send(Native.SciGetSelectionNCaret, i),
            Send(Native.SciGetSelectionNAnchorVirtualSpace, i),
            Send(Native.SciGetSelectionNCaretVirtualSpace, i));

        private static (nint, nint, nint, nint) ReadRectangle() => (
            Send(Native.SciGetRectangularSelectionAnchor),
            Send(Native.SciGetRectangularSelectionCaret),
            Send(Native.SciGetRectangularSelectionAnchorVirtualSpace),
            Send(Native.SciGetRectangularSelectionCaretVirtualSpace));
    }

    /// <summary>
    /// 選択ごとに変換を適用する。全体で1回のUndoにまとめ、後ろの選択から処理して位置のずれを防ぐ。
    /// action が null を返した選択は変えない。firstOnly なら最初の選択だけを処理する。
    /// 文書に文字コードとして読めないバイトがあれば何も変えずに false を返す。last は最後に適用した編集（なければ null）。
    /// </summary>
    public static bool TryApply(Func<string, int, int, TextEdit?> action, out TextEdit? last, bool lineLevel = false, bool firstOnly = false)
    {
        last = null;
        var encoding = CurrentEncoding;
        if (!RoundTrips(GetBytes(), encoding))
        {
            return false;
        }

        var count = firstOnly ? 1 : (int)Send(Native.SciGetSelections);
        var all = new List<SelectionSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var from = (int)Send(Native.SciGetSelectionNStart, i);
            var to = (int)Send(Native.SciGetSelectionNEnd, i);
            all.Add(new SelectionSpan(from, to, (int)Send(Native.SciLineFromPosition, from), (int)Send(Native.SciLineFromPosition, to)));
        }

        // 行単位の操作は編集範囲が行全体へ広がるので、同じ行の選択を1回にまとめる
        var selections = lineLevel ? TextFormatter.OnePerLine(all) : [.. all.OrderByDescending(s => s.Start)];
        var finalStart = 0;
        var finalEnd = 0;

        Send(Native.SciBeginUndoAction);
        try
        {
            foreach (var selection in selections)
            {
                var bytes = GetBytes();
                var text = encoding.GetString(bytes);
                var start = encoding.GetCharCount(bytes, 0, selection.Start);
                var end = encoding.GetCharCount(bytes, 0, selection.End);
                if (action(text, start, end) is not { } edit)
                {
                    continue;
                }

                var byteStart = encoding.GetByteCount(text.AsSpan(0, edit.Start));
                var byteEnd = encoding.GetByteCount(text.AsSpan(0, edit.Start + edit.Length));
                if (edit.Length > 0 || edit.Replacement.Length > 0)
                {
                    Replace(byteStart, byteEnd, encoding.GetBytes(edit.Replacement));
                }

                var updated = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
                finalStart = encoding.GetByteCount(updated.AsSpan(0, edit.SelectionStart));
                finalEnd = encoding.GetByteCount(updated.AsSpan(0, edit.SelectionEnd));
                last = edit;
            }
        }
        finally
        {
            Send(Native.SciEndUndoAction);
        }

        if (count == 1 && last is not null)
        {
            Send(Native.SciSetSel, finalStart, finalEnd);
        }

        Send(Native.SciGrabFocus);
        return true;
    }

    /// <summary>
    /// 全選択（空の選択を含む文字位置）を1回で渡し、返った小さな編集の一覧を後ろから適用する（1回のUndo）。
    /// 選択は変えず、各編集に合わせてScintillaが端点をずらす。文字コードとして読めない文書では何も変えずに false。
    /// count は適用した編集の数。
    /// </summary>
    public static bool TryApplyEdits(Func<string, IReadOnlyList<(int Start, int End)>, IReadOnlyList<TextEdit>> action, out int count)
    {
        count = 0;
        var encoding = CurrentEncoding;
        var bytes = GetBytes();
        if (!RoundTrips(bytes, encoding))
        {
            return false;
        }

        var text = encoding.GetString(bytes);
        var selections = new List<(int, int)>();
        var total = (int)Send(Native.SciGetSelections);
        for (var i = 0; i < total; i++)
        {
            var from = (int)Send(Native.SciGetSelectionNStart, i);
            var to = (int)Send(Native.SciGetSelectionNEnd, i);
            selections.Add((encoding.GetCharCount(bytes, 0, from), encoding.GetCharCount(bytes, 0, to)));
        }

        var edits = action(text, selections).Where(e => e.Length > 0 || e.Replacement.Length > 0).OrderByDescending(e => e.Start).ToList();
        if (edits.Count == 0)
        {
            Send(Native.SciGrabFocus);
            return true;
        }

        Send(Native.SciBeginUndoAction);
        try
        {
            // 後ろから適用するので、前の編集の位置は元の文書のまま使える
            foreach (var edit in edits)
            {
                var byteStart = encoding.GetByteCount(text.AsSpan(0, edit.Start));
                var byteEnd = byteStart + encoding.GetByteCount(text.AsSpan(edit.Start, edit.Length));
                Replace(byteStart, byteEnd, encoding.GetBytes(edit.Replacement));
                count++;
            }
        }
        finally
        {
            Send(Native.SciEndUndoAction);
        }

        Send(Native.SciGrabFocus);
        return true;
    }

    /// <summary>
    /// 文書全体を、変更された行の範囲だけ置き換える（1回のUndo）。
    /// 文書に文字コードとして読めないバイトがあれば（updated は文字化けした文字列から作られているので）何も変えずに false を返す。
    /// </summary>
    public static bool ReplaceAll(string updated)
    {
        var encoding = CurrentEncoding;
        var current = GetBytes();
        if (!RoundTrips(current, encoding))
        {
            return false;
        }

        var next = encoding.GetBytes(updated);
        var prefix = 0;
        while (prefix < current.Length && prefix < next.Length && current[prefix] == next[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < current.Length - prefix && suffix < next.Length - prefix && current[current.Length - 1 - suffix] == next[next.Length - 1 - suffix])
        {
            suffix++;
        }

        Send(Native.SciBeginUndoAction);
        try
        {
            Replace(prefix, current.Length - suffix, next.AsSpan(prefix, next.Length - prefix - suffix).ToArray());
        }
        finally
        {
            Send(Native.SciEndUndoAction);
        }

        Send(Native.SciGrabFocus);
        return true;
    }

    private static void Replace(int start, int end, byte[] replacement)
    {
        Send(Native.SciSetTargetStart, start);
        Send(Native.SciSetTargetEnd, end);
        fixed (byte* data = replacement.Length == 0 ? new byte[1] : replacement)
        {
            Send(Native.SciReplaceTarget, replacement.Length, (nint)data);
        }
    }
}
