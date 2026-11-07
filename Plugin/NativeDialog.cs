using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownTrellis.Plugin;

internal enum DialogFieldKind { Text, Number, Check }

/// <summary>入力欄の定義。結果は Value（文字）または Checked に入る。</summary>
internal sealed class DialogField(DialogFieldKind kind, string label, string value = "", bool isChecked = false, string tag = "")
{
    public DialogFieldKind Kind { get; } = kind;

    public string Label { get; } = label;

    public string Tag { get; } = tag;

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
    private const ushort ClassButton = 0x80;
    private const ushort ClassEdit = 0x81;
    private const ushort ClassStatic = 0x82;

    private const short DialogWidth = 230;
    private const short ContentWidth = 214;

    private static IReadOnlyList<DialogField>? _fields;
    private static IReadOnlyList<DialogButton>? _extras;

    /// <summary>OKなら true。値は fields に入る。</summary>
    public static bool Show(nint owner, string title, IReadOnlyList<DialogField> fields, string okLabel, string? cancelLabel, IReadOnlyList<DialogButton>? extras = null)
    {
        extras ??= [];
        var template = BuildTemplate(title, fields, okLabel, cancelLabel, extras);
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

    private static byte[] BuildTemplate(string title, IReadOnlyList<DialogField> fields, string okLabel, string? cancelLabel, IReadOnlyList<DialogButton> extras)
    {
        // 各項目の位置を先に求める
        var items = new List<(uint Style, short X, short Y, short W, short H, ushort Id, ushort Class, string Text)>();
        short y = 7;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var id = (ushort)(FirstField + i);
            if (field.Kind == DialogFieldKind.Check)
            {
                items.Add((WsTabStop | BsAutoCheckBox, 8, y, ContentWidth, 11, id, ClassButton, field.Label));
                y += 13;
            }
            else
            {
                items.Add((0, 8, y, ContentWidth, 9, 0xFFFF, ClassStatic, field.Label));
                y += 10;
                var style = WsTabStop | WsBorder | EsAutoHScroll | (field.Kind == DialogFieldKind.Number ? EsNumber : 0);
                items.Add((style, 8, y, ContentWidth, 13, id, ClassEdit, field.Value));
                y += 17;
            }
        }

        y += 3;
        short x = 8;
        items.Add((WsTabStop | BsDefPushButton, x, y, 44, 14, IdOk, ClassButton, okLabel));
        x += 48;
        for (var i = 0; i < extras.Count; i++)
        {
            items.Add((WsTabStop, x, y, 62, 14, (ushort)(FirstExtra + i), ClassButton, extras[i].Label));
            x += 66;
        }

        if (cancelLabel is not null)
        {
            items.Add((WsTabStop, x, y, 50, 14, IdCancel, ClassButton, cancelLabel));
        }

        y += 21;

        var buffer = new List<byte>(1024);
        WriteUInt(buffer, DsSetFont | DsModalFrame | DsCenter | WsPopup | WsCaption | WsSysMenu);
        WriteUInt(buffer, 0);
        WriteUShort(buffer, (ushort)items.Count);
        WriteShort(buffer, 0);
        WriteShort(buffer, 0);
        WriteShort(buffer, DialogWidth);
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
            if (_fields[i].Kind == DialogFieldKind.Check)
            {
                Native.SendMessage(Native.GetDlgItem(dialog, FirstField + i), BmSetCheck, _fields[i].Checked ? 1 : 0, 0);
            }
        }
    }

    private static void SelectFirstEdit(nint dialog)
    {
        for (var i = 0; i < _fields!.Count; i++)
        {
            if (_fields[i].Kind != DialogFieldKind.Check)
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
            if (_fields[i].Kind == DialogFieldKind.Check)
            {
                _fields[i].Checked = Native.SendMessage(item, BmGetCheck, 0, 0) == 1;
            }
            else
            {
                var length = Native.GetWindowText(item, buffer, 2048);
                _fields[i].Value = new string(buffer, 0, length);
            }
        }
    }
}
