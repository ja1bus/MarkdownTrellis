using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownTrellis.Plugin;

/// <summary>
/// WebView2を起動できないPC用の代替表示（IEエンジンのWebBrowserコントロール）。
/// 表示するHTMLはサニタイズ済みでスクリプトを含まない。リンクは文字にして画面遷移を防ぐ。
/// </summary>
internal sealed unsafe class LegacyPreview
{
    // 番号はWindows SDKの ExDisp.h、MsHTML.h、OCIdl.h の継承順に基づく（IUnknown、IDispatchを含む）
    private const int BrowserNavigate = 11;
    private const int BrowserGetDocument = 18;
    private const int BrowserGetReadyState = 56;
    private const int BrowserPutSilent = 60;
    private const int DocumentGetBody = 9;
    private const int ElementPutInnerHtml = 57;
    private const int PersistLoad = 5;
    private const int ReadyStateComplete = 4;

    private const string BrowserClsid = "{8856F961-340A-11D0-A96B-00C04FD705A2}";
    private const string Shell = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><style>"
        + "body{margin:0;padding:10px 14px;font:14px/1.6 'Segoe UI','Yu Gothic UI',Meiryo,sans-serif;color:#222;background:#fff;word-wrap:break-word}"
        + "h1,h2{border-bottom:1px solid #ccc;padding-bottom:.2em}h1,h2,h3,h4,h5,h6{margin:1em 0 .5em;line-height:1.3}"
        + "pre{background:#f3f3f3;padding:8px 10px;overflow:auto}code{background:#f3f3f3;padding:.1em .3em;font-family:Consolas,'Courier New',monospace}pre code{padding:0;background:none}"
        + "blockquote{margin:0 0 1em;padding:0 0 0 12px;border-left:4px solid #ccc;color:#666}table{border-collapse:collapse;margin:0 0 1em}th,td{border:1px solid #ccc;padding:4px 8px}th{background:#f3f3f3}"
        + "hr{border:0;border-top:1px solid #ccc;margin:1em 0}ul.contains-task-list{list-style:none;padding-left:1.2em}"
        + ".link{color:#0b63c5;text-decoration:underline}.blocked-image{border:1px dashed #ccc;padding:0 4px;color:#666}"
        + ".markdown-alert{border-left:4px solid #0b63c5;padding:2px 0 2px 12px;margin:0 0 1em}.markdown-alert-title{font-weight:bold}"
        + "</style></head><body></body></html>";

    private static readonly Guid IidBrowser = new("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E");
    private static readonly Guid IidDocument = new("332C4425-26CB-11D0-B483-00C04FD90119");
    private static readonly Guid IidElement = new("3050F1FF-98B5-11CF-BB82-00AA00BDCE0B");
    private static readonly Guid IidPersist = new("7FD52380-4E07-101B-AE2D-08002B2EC713");

    private enum Stage { NavigatingBlank, LoadingShell, Ready }

    private nint _window;
    private nint _browser;
    private Stage _stage;
    private string? _pending;

    public bool Create(nint parent, nint instance)
    {
        if (Native.AtlAxWinInit() == 0)
        {
            return false;
        }

        _window = Native.CreateWindowEx(0, "AtlAxWin", BrowserClsid, Native.WsChild | Native.WsVisible, 0, 0, 100, 100, parent, 0, instance, 0);
        if (_window == 0 || Native.AtlAxGetControl(_window, out var control) < 0 || control == 0)
        {
            return false;
        }

        var hr = Com.QueryInterface(control, IidBrowser, out _browser);
        Com.Release(control);
        if (hr < 0)
        {
            return false;
        }

        // スクリプトのエラーなどのダイアログを出さない
        Com.Call(_browser, BrowserPutSilent, -1);
        var url = Marshal.StringToBSTR("about:blank");
        try
        {
            var navigate = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, nint, int>)Com.Slot(_browser, BrowserNavigate);
            return navigate(_browser, url, 0, 0, 0, 0) >= 0;
        }
        finally
        {
            Marshal.FreeBSTR(url);
        }
    }

    public void Move(int x, int y, int width, int height)
    {
        if (_window != 0)
        {
            Native.MoveWindow(_window, x, y, width, height, 1);
        }
    }

    public void SetBody(string html)
    {
        _pending = html;
        Advance();
    }

    public void SetText(string text) => SetBody("<p>" + WebUtility.HtmlEncode(text) + "</p>");

    /// <summary>読み込みの完了通知を受けないので、タイマーから呼んで段階を進める。</summary>
    public void Advance()
    {
        if (_browser == 0 || Com.Get32(_browser, BrowserGetReadyState, out var state) < 0 || state != ReadyStateComplete)
        {
            return;
        }

        switch (_stage)
        {
            case Stage.NavigatingBlank:
                LoadShell();
                _stage = Stage.LoadingShell;
                break;
            case Stage.LoadingShell:
                _stage = Stage.Ready;
                goto case Stage.Ready;
            case Stage.Ready:
                if (_pending is { } html && WriteBody(html))
                {
                    _pending = null;
                }

                break;
        }
    }

    public bool IsWaiting => _stage != Stage.Ready || _pending is not null;

    private void LoadShell()
    {
        Com.Check(Com.Get(_browser, BrowserGetDocument, out var document));
        try
        {
            Com.Check(Com.QueryInterface(document, IidPersist, out var persist));
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Shell)).ToArray();
            fixed (byte* data = bytes)
            {
                var stream = Native.SHCreateMemStream(data, (uint)bytes.Length);
                Com.Check(Com.Call(persist, PersistLoad, stream));
                Com.Release(stream);
            }

            Com.Release(persist);
        }
        finally
        {
            Com.Release(document);
        }
    }

    private bool WriteBody(string html)
    {
        if (Com.Get(_browser, BrowserGetDocument, out var dispatch) < 0 || dispatch == 0)
        {
            return false;
        }

        nint document = 0, body = 0, element = 0;
        var text = Marshal.StringToBSTR(html);
        try
        {
            return Com.QueryInterface(dispatch, IidDocument, out document) >= 0
                && Com.Get(document, DocumentGetBody, out body) >= 0 && body != 0
                && Com.QueryInterface(body, IidElement, out element) >= 0
                && Com.Call(element, ElementPutInnerHtml, text) >= 0;
        }
        finally
        {
            Marshal.FreeBSTR(text);
            foreach (var item in new[] { element, body, document, dispatch })
            {
                if (item != 0)
                {
                    Com.Release(item);
                }
            }
        }
    }
}

/// <summary>代替表示で使うWindows標準のボタンと状態表示。パネルの上部に折り返して並べる。</summary>
internal sealed class LegacyToolbar
{
    public const int FirstCommandId = 2000;

    private const int Margin = 2;

    private static readonly (string Key, string? Label)[] Items =
    [
        ("bold", "B"), ("italic", "I"), ("strike", "S"), ("code", "</>"),
        ("h1", "H1"), ("h2", "H2"), ("h3", "H3"),
        ("ul", "•"), ("ol", "1."), ("task", "☑"), ("quote", "❝"),
        ("codeblock", "{ }"), ("table", "▦"), ("link", null), ("image", null), ("hr", "―"),
        ("copyMarkdown", null), ("copyRich", null),
    ];

    public const int ModeComboId = 3000;
    public const int FeaturesButtonId = 3001;

    private const uint CbAddString = 0x0143;
    private const uint CbSetCurSel = 0x014E;
    private const uint CbGetCurSel = 0x0147;
    private const uint CbsDropDownList = 3;
    private const uint WmSetFont = 0x0030;
    private const int ComboDropHeight = 120;

    private readonly List<(nint Window, int Width, bool IsCombo)> _buttons = [];
    private readonly nint _parent;
    private nint _status;
    private nint _mode;
    private nint _font;
    private string _statusText = string.Empty;

    public LegacyToolbar(nint parent) => _parent = parent;

    public void Create(nint instance, IReadOnlyList<PluginCommand> commands, Func<string, string> text)
    {
        _font = Native.GetStockObject(17);
        var scale = Native.GetDpiForWindow(_parent) / 96.0;
        foreach (var (key, label) in Items)
        {
            var index = commands.ToList().FindIndex(c => c.Key == key);
            if (index >= 0)
            {
                var caption = label ?? text(key).TrimEnd('.');
                AddButton(instance, caption, FirstCommandId + index, scale);
            }
        }

        _mode = Native.CreateWindowEx(0, "COMBOBOX", string.Empty, Native.WsChild | Native.WsVisible | CbsDropDownList, 0, 0, 100, ComboDropHeight, _parent, ModeComboId, instance, 0);
        Native.SendMessage(_mode, WmSetFont, _font, 1);
        Native.SendMessageText(_mode, CbAddString, 0, "GFM");
        Native.SendMessageText(_mode, CbAddString, 0, "CommonMark");
        _buttons.Add((_mode, (int)(100 * scale), true));
        AddButton(instance, text("features"), FeaturesButtonId, scale);

        _status = Native.CreateWindowEx(0, "STATIC", string.Empty, Native.WsChild | Native.WsVisible, 0, 0, 100, 20, _parent, 0, instance, 0);
        Native.SendMessage(_status, WmSetFont, _font, 1);
    }

    private void AddButton(nint instance, string caption, int id, double scale)
    {
        var width = (int)((caption.Length <= 3 ? 30 : 16 + (caption.Length * 13)) * scale);
        var button = Native.CreateWindowEx(0, "BUTTON", caption, Native.WsChild | Native.WsVisible, 0, 0, width, 24, _parent, id, instance, 0);
        Native.SendMessage(button, WmSetFont, _font, 1);
        _buttons.Add((button, width, false));
    }

    public void SetStatus(string text)
    {
        _statusText = text;
        Native.SetWindowText(_status, text);
    }

    public void SetMode(bool isCommonMark) => Native.SendMessage(_mode, CbSetCurSel, isCommonMark ? 1 : 0, 0);

    public bool IsCommonMarkSelected => Native.SendMessage(_mode, CbGetCurSel, 0, 0) == 1;

    /// <summary>状態表示の文字が、指定の幅で必要とする高さを求める（空でも1行分）。</summary>
    private int MeasureStatus(int width)
    {
        var hdc = Native.GetDC(_status);
        var previous = Native.SelectObject(hdc, _font);
        var rect = new Rect { Right = Math.Max(1, width) };
        const uint DtWordBreak = 0x10;
        const uint DtCalcRect = 0x400;
        var height = Native.DrawText(hdc, _statusText.Length > 0 ? _statusText : " ", -1, ref rect, DtWordBreak | DtCalcRect);
        Native.SelectObject(hdc, previous);
        Native.ReleaseDC(_status, hdc);
        return height;
    }

    /// <summary>ボタンを折り返して配置し、使った高さを返す。</summary>
    public int Layout(int width)
    {
        var scale = Native.GetDpiForWindow(_parent) / 96.0;
        var height = (int)(24 * scale);
        var x = Margin;
        var y = Margin;
        foreach (var (window, buttonWidth, isCombo) in _buttons)
        {
            if (x + buttonWidth + Margin > width && x > Margin)
            {
                x = Margin;
                y += height + Margin;
            }

            Native.MoveWindow(window, x, y, buttonWidth, isCombo ? (int)(ComboDropHeight * scale) : height, 1);
            x += buttonWidth + Margin;
        }

        y += height + Margin;

        // 状態表示は文字に必要な高さだけを使う
        var statusWidth = Math.Max(0, width - (Margin * 2));
        var statusHeight = MeasureStatus(statusWidth);
        Native.MoveWindow(_status, Margin, y, statusWidth, statusHeight, 1);
        return y + statusHeight + Margin;
    }
}
