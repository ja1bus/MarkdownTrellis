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

    /// <summary>
    /// 選択ごとに変換を適用する。全体で1回のUndoにまとめ、後ろの選択から処理して位置のずれを防ぐ。
    /// </summary>
    public static TextEdit? Apply(Func<string, int, int, TextEdit> action, bool lineLevel = false)
    {
        var count = (int)Send(Native.SciGetSelections);
        var all = new List<SelectionSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var from = (int)Send(Native.SciGetSelectionNStart, i);
            var to = (int)Send(Native.SciGetSelectionNEnd, i);
            all.Add(new SelectionSpan(from, to, (int)Send(Native.SciLineFromPosition, from), (int)Send(Native.SciLineFromPosition, to)));
        }

        // 行単位の操作は編集範囲が行全体へ広がるので、同じ行の選択を1回にまとめる
        var selections = lineLevel ? TextFormatter.OnePerLine(all) : [.. all.OrderByDescending(s => s.Start)];
        var encoding = CurrentEncoding;
        TextEdit? last = null;
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
                var edit = action(text, start, end);
                var byteStart = encoding.GetByteCount(text.AsSpan(0, edit.Start));
                var byteEnd = encoding.GetByteCount(text.AsSpan(0, edit.Start + edit.Length));
                Replace(byteStart, byteEnd, encoding.GetBytes(edit.Replacement));

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

        if (count == 1)
        {
            Send(Native.SciSetSel, finalStart, finalEnd);
        }

        Send(Native.SciGrabFocus);
        return last;
    }

    /// <summary>文書全体を、変更された行の範囲だけ置き換える（1回のUndo）。</summary>
    public static void ReplaceAll(string updated)
    {
        var encoding = CurrentEncoding;
        var current = GetBytes();
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
