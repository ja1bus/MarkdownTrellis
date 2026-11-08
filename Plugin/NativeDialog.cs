using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownTrellis.Plugin;

/// <summary>Radioは連続する同じTagの項目で1組。Labelは説明文、Headingは区切りの見出し。</summary>
internal enum DialogFieldKind { Text, Number, Check, Radio, Label, Heading }

/// <summary>入力欄の定義。結果は Value（文字）または Checked に入る。</summary>
internal sealed class DialogField(DialogFieldKind kind, string label, string value = "", bool isChecked = false, string tag = "", int indent = 0)
{
    public DialogFieldKind Kind { get; } = kind;

    public string Label { get; } = label;

    public string Tag { get; } = tag;

    public int Indent { get; } = indent;

    public string Value { get; set; } = value;

    public bool Checked { get; set; } = isChecked;
}

/// <summary>追加のボタン。押してもダイアログは閉じず、チェックだけを変える。</summary>
internal sealed record DialogButton(string Label, Action<IReadOnlyList<DialogField>> Change);

/// <summary>WebView2を使わない入力画面。メモリ上のテンプレートからWindows標準のダイアログを作る。</summary>
internal static unsafe class NativeDialog
{
    private const uint WmInitDialog = 0x0110;
    private const uint WmCommand = 0x0111;
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int FirstField = 100;
    private const int FirstExtra = 200;
    private const uint BmGetCheck = 0x00F0;
    private const uint BmSetCheck = 0x00F1;
    private const uint EmSetSel = 0x00B1;

    private const uint DsSetFont = 0x40;
    private const uint DsModalFrame = 0x80;
    private const uint DsCenter = 0x800;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsTabStop = 0x00010000;
    private const uint WsBorder = 0x00800000;
    private const uint EsAutoHScroll = 0x80;
    private const uint EsNumber = 0x2000;
    private const uint BsDefPushButton = 1;
    private const uint BsAutoCheckBox = 3;
    private const uint BsAutoRadioButton = 9;
    private const uint WsGroup = 0x00020000;
    private const ushort ClassButton = 0x80;
    private const ushort ClassEdit = 0x81;
    private const ushort ClassStatic = 0x82;

    private const short DefaultWidth = 230;

    private static IReadOnlyList<DialogField>? _fields;
    private static IReadOnlyList<DialogButton>? _extras;

    /// <summary>OKなら true。値は fields に入る。</summary>
    public static bool Show(nint owner, string title, IReadOnlyList<DialogField> fields, string okLabel, string? cancelLabel, IReadOnlyList<DialogButton>? extras = null, short width = DefaultWidth)
    {
        extras ??= [];
        var template = BuildTemplate(title, fields, okLabel, cancelLabel, extras, width);
        _fields = fields;
        _extras = extras;
        try
        {
            fixed (byte* data = template)
            {
                return Native.DialogBoxIndirectParam(Native.GetModuleHandle("MarkdownTrellis.dll"), data, owner, &Proc, 0) == 1;
            }
        }
        finally
        {
            _fields = null;
            _extras = null;
        }
    }

    private static byte[] BuildTemplate(string title, IReadOnlyList<DialogField> fields, string okLabel, string? cancelLabel, IReadOnlyList<DialogButton> extras, short dialogWidth)
    {
        // 各項目の位置を先に求める
        var contentWidth = (short)(dialogWidth - 16);
        var items = new List<(uint Style, short X, short Y, short W, short H, ushort Id, ushort Class, string Text)>();
        short y = 7;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var id = (ushort)(FirstField + i);
            var x = (short)(8 + field.Indent);
            var width = (short)(contentWidth - field.Indent);
            if (field.Kind == DialogFieldKind.Check)
            {
                items.Add((WsTabStop | WsGroup | BsAutoCheckBox, x, y, width, 11, id, ClassButton, field.Label));
                y += 13;
            }
            else if (field.Kind == DialogFieldKind.Radio)
            {
                // 組の最初の項目にだけWS_GROUPを付け、矢印キーで組の中を移動できるようにする
                var first = i == 0 || fields[i - 1].Kind != DialogFieldKind.Radio || fields[i - 1].Tag != field.Tag;
                items.Add(((first ? WsTabStop | WsGroup : 0) | BsAutoRadioButton, x, y, width, 11, id, ClassButton, field.Label));
                y += 12;
            }
            else if (field.Kind is DialogFieldKind.Label or DialogFieldKind.Heading)
            {
                if (field.Kind == DialogFieldKind.Heading && i > 0)
                {
                    y += 4;
                }

                var height = (short)(EstimateLines(field.Label, width) * 9);
                items.Add((WsGroup, x, y, width, height, 0xFFFF, ClassStatic, field.Label));
                y += (short)(height + 2);
            }
            else
            {
                items.Add((WsGroup, x, y, width, 9, 0xFFFF, ClassStatic, field.Label));
                y += 10;
                var style = WsTabStop | WsBorder | EsAutoHScroll | (field.Kind == DialogFieldKind.Number ? EsNumber : 0);
                items.Add((style, x, y, width, 13, id, ClassEdit, field.Value));
                y += 17;
            }
        }

        y += 3;
        short buttonX = 8;
        items.Add((WsTabStop | WsGroup | BsDefPushButton, buttonX, y, 44, 14, IdOk, ClassButton, okLabel));
        buttonX += 48;
        for (var i = 0; i < extras.Count; i++)
        {
            items.Add((WsTabStop | WsGroup, buttonX, y, 62, 14, (ushort)(FirstExtra + i), ClassButton, extras[i].Label));
            buttonX += 66;
        }

        if (cancelLabel is not null)
        {
            items.Add((WsTabStop | WsGroup, buttonX, y, 50, 14, IdCancel, ClassButton, cancelLabel));
        }

        y += 21;

        var buffer = new List<byte>(1024);
        WriteUInt(buffer, DsSetFont | DsModalFrame | DsCenter | WsPopup | WsCaption | WsSysMenu);
        WriteUInt(buffer, 0);
        WriteUShort(buffer, (ushort)items.Count);
        WriteShort(buffer, 0);
        WriteShort(buffer, 0);
        WriteShort(buffer, dialogWidth);
        WriteShort(buffer, y);
        WriteUShort(buffer, 0);
        WriteUShort(buffer, 0);
        WriteString(buffer, title);
        WriteUShort(buffer, 9);
        WriteString(buffer, "Yu Gothic UI");

        foreach (var item in items)
        {
            while (buffer.Count % 4 != 0)
            {
                buffer.Add(0);
            }

            WriteUInt(buffer, WsChild | WsVisible | item.Style);
            WriteUInt(buffer, 0);
            WriteShort(buffer, item.X);
            WriteShort(buffer, item.Y);
            WriteShort(buffer, item.W);
            WriteShort(buffer, item.H);
            WriteUShort(buffer, item.Id);
            WriteUShort(buffer, 0xFFFF);
            WriteUShort(buffer, item.Class);
            WriteString(buffer, item.Text);
            WriteUShort(buffer, 0);
        }

        return [.. buffer];
    }

    /// <summary>説明文の行数の目安。全角は半角2文字分、幅は約4ダイアログ単位を半角1文字とする。</summary>
    private static int EstimateLines(string text, int width)
    {
        var perLine = Math.Max(10, (width / 4) - 2);
        var lines = 0;
        foreach (var paragraph in text.Split('\n'))
        {
            var units = paragraph.Sum(c => c < 0x2E80 ? 1 : 2);
            lines += Math.Max(1, (units + perLine - 1) / perLine);
        }

        return lines;
    }

    private static void WriteUInt(List<byte> buffer, uint value) => buffer.AddRange(BitConverter.GetBytes(value));

    private static void WriteUShort(List<byte> buffer, ushort value) => buffer.AddRange(BitConverter.GetBytes(value));

    private static void WriteShort(List<byte> buffer, short value) => buffer.AddRange(BitConverter.GetBytes(value));

    private static void WriteString(List<byte> buffer, string text)
    {
        buffer.AddRange(Encoding.Unicode.GetBytes(text));
        WriteUShort(buffer, 0);
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint dialog, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                    SyncChecks(dialog);
                    SelectFirstEdit(dialog);
                    return 1;
                case WmCommand:
                    var id = (int)(wParam & 0xFFFF);
                    if (id == IdOk)
                    {
                        ReadValues(dialog);
                        Native.EndDialog(dialog, 1);
                        return 1;
                    }

                    if (id == IdCancel)
                    {
                        Native.EndDialog(dialog, 0);
                        return 1;
                    }

                    if (id >= FirstExtra && _extras is { } extras && id - FirstExtra < extras.Count && _fields is { } fields)
                    {
                        ReadValues(dialog);
                        extras[id - FirstExtra].Change(fields);
                        SyncChecks(dialog);
                        return 1;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            PluginHost.Log(ex);
        }

        return 0;
    }

    private static void SyncChecks(nint dialog)
    {
        for (var i = 0; i < _fields!.Count; i++)
        {
            if (_fields[i].Kind is DialogFieldKind.Check or DialogFieldKind.Radio)
            {
                Native.SendMessage(Native.GetDlgItem(dialog, FirstField + i), BmSetCheck, _fields[i].Checked ? 1 : 0, 0);
            }
        }
    }

    private static void SelectFirstEdit(nint dialog)
    {
        for (var i = 0; i < _fields!.Count; i++)
        {
            if (_fields[i].Kind is DialogFieldKind.Text or DialogFieldKind.Number)
            {
                var edit = Native.GetDlgItem(dialog, FirstField + i);
                Native.SendMessage(edit, EmSetSel, 0, -1);
                return;
            }
        }
    }

    private static void ReadValues(nint dialog)
    {
        var buffer = stackalloc char[2048];
        for (var i = 0; i < _fields!.Count; i++)
        {
            var item = Native.GetDlgItem(dialog, FirstField + i);
            if (_fields[i].Kind is DialogFieldKind.Check or DialogFieldKind.Radio)
            {
                _fields[i].Checked = Native.SendMessage(item, BmGetCheck, 0, 0) == 1;
            }
            else if (_fields[i].Kind is DialogFieldKind.Text or DialogFieldKind.Number)
            {
                var length = Native.GetWindowText(item, buffer, 2048);
                _fields[i].Value = new string(buffer, 0, length);
            }
        }
    }
}
