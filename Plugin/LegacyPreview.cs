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

/// <summary>
/// 代替表示で使うWindows標準のボタンと状態表示。パネルの上部に折り返して並べる。
/// 使う頻度の低い書式は［その他］、追加構文と設定は歯車のメニューにまとめる。IEの表示は狭いパネルで使うことが多いので、メニューの2つを並べ、コピーを最後の行に置く。
/// </summary>
internal sealed class LegacyToolbar
{
    public const int FirstCommandId = 2000;
    public const int MoreButtonId = 3002;
    public const int SettingsButtonId = 3003;
    public const int CloseStatusId = 3004;

    /// <summary>歯車メニューの［追加構文...］。コマンドの番号と重ならない値にする。</summary>
    public const int FeaturesMenuId = 3100;

    /// <summary>ボタンに出す文字。nullは表示言語の文字を使う。</summary>
    private static readonly (string Key, string? Label)[] Items =
    [
        ("h1", "H1"), ("h2", "H2"), ("h3", "H3"), ("bold", "B"),
        ("ul", "•"), ("ol", "1."), ("task", "☑"),
        ("link", null), ("table", "▦"), ("code", "</>"), ("codeblock", "{ }"),
    ];

    private static readonly string[] MoreItems = ["italic", "strike", "quote", "image", "hr", string.Empty, "copyRichSelection"];

    private const int Margin = 2;
    private const uint WmSetFont = 0x0030;

    private readonly List<ToolButton> _buttons = [];
    private readonly nint _parent;
    private nint _status;
    private nint _closeStatus;
    private nint _font;
    private string _statusText = string.Empty;

    private sealed class ToolButton
    {
        public nint Window;
        public int Width;
        public string Key = string.Empty;
        public bool Localized;
        public bool BreakBefore;
    }

    public LegacyToolbar(nint parent) => _parent = parent;

    public void Create(nint instance, IReadOnlyList<PluginCommand> commands, Func<string, string> text)
    {
        _font = Native.GetStockObject(17);
        var scale = Scale;
        foreach (var (key, label) in Items)
        {
            var index = IndexOf(commands, key);
            if (index >= 0)
            {
                AddButton(instance, key, label ?? text(key).TrimEnd('.'), label is null, FirstCommandId + index, scale);
            }
        }

        AddButton(instance, "more", text("more") + " ▾", true, MoreButtonId, scale);
        AddButton(instance, "settingsMenu", "⚙ ▾", false, SettingsButtonId, scale);

        // コピーの2つは1つのまとまりとして、狭いときは次の行へまとめて移す
        AddButton(instance, "copyMarkdown", text("copyMarkdown"), true, FirstCommandId + IndexOf(commands, "copyMarkdown"), scale).BreakBefore = true;
        AddButton(instance, "copyRich", text("copyRich"), true, FirstCommandId + IndexOf(commands, "copyRich"), scale);

        _status = Native.CreateWindowEx(0, "STATIC", string.Empty, Native.WsChild, 0, 0, 100, 20, _parent, 0, instance, 0);
        Native.SendMessage(_status, WmSetFont, _font, 1);
        _closeStatus = Native.CreateWindowEx(0, "BUTTON", "×", Native.WsChild, 0, 0, 20, 20, _parent, CloseStatusId, instance, 0);
        Native.SendMessage(_closeStatus, WmSetFont, _font, 1);
    }

    private double Scale => Native.GetDpiForWindow(_parent) / 96.0;

    private static int IndexOf(IReadOnlyList<PluginCommand> commands, string key)
    {
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private static int WidthOf(string caption, double scale) => (int)((caption.Length <= 3 ? 30 : 16 + (caption.Length * 13)) * scale);

    private ToolButton AddButton(nint instance, string key, string caption, bool localized, int id, double scale)
    {
        var width = WidthOf(caption, scale);
        var window = Native.CreateWindowEx(0, "BUTTON", caption, Native.WsChild | Native.WsVisible, 0, 0, width, 24, _parent, id, instance, 0);
        Native.SendMessage(window, WmSetFont, _font, 1);
        var button = new ToolButton { Window = window, Width = width, Key = key, Localized = localized };
        _buttons.Add(button);
        return button;
    }

    /// <summary>表示言語を変えたときに、文字のボタンを付け直す。</summary>
    public void Relabel(Func<string, string> text)
    {
        var scale = Scale;
        foreach (var button in _buttons.Where(b => b.Localized))
        {
            var caption = button.Key == "more" ? text("more") + " ▾" : text(button.Key).TrimEnd('.');
            Native.SetWindowText(button.Window, caption);
            button.Width = WidthOf(caption, scale);
        }
    }

    public void SetStatus(string text)
    {
        _statusText = text;
        Native.SetWindowText(_status, text);
        var show = text.Length > 0 ? 5 : 0;
        Native.ShowWindow(_status, show);
        Native.ShowWindow(_closeStatus, show);
    }

    /// <summary>［その他］または歯車のメニューを出し、選ばれた項目のIDを返す（取り消しは0）。</summary>
    public int ShowMenu(int buttonId, IReadOnlyList<PluginCommand> commands, Func<string, string> text)
    {
        var menu = Native.CreatePopupMenu();
        try
        {
            if (buttonId == MoreButtonId)
            {
                foreach (var key in MoreItems)
                {
                    if (key.Length == 0)
                    {
                        Native.AppendMenu(menu, Native.MfSeparator, 0, null);
                        continue;
                    }

                    Native.AppendMenu(menu, Native.MfString, (nuint)(FirstCommandId + IndexOf(commands, key)), text(key));
                }
            }
            else
            {
                Native.AppendMenu(menu, Native.MfString, FeaturesMenuId, text("featuresMenu"));
                Native.AppendMenu(menu, Native.MfString, (nuint)(FirstCommandId + IndexOf(commands, "settings")), text("settings"));
            }

            var button = Native.GetDlgItem(_parent, buttonId);
            Native.GetWindowRect(button, out var rect);
            return Native.TrackPopupMenu(menu, Native.TpmReturnCmd, rect.Left, rect.Bottom, 0, _parent, 0);
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    /// <summary>状態表示の文字が、指定の幅で必要とする高さを求める。</summary>
    private int MeasureStatus(int width)
    {
        var hdc = Native.GetDC(_status);
        var previous = Native.SelectObject(hdc, _font);
        var rect = new Rect { Right = Math.Max(1, width) };
        const uint DtWordBreak = 0x10;
        const uint DtCalcRect = 0x400;
        var height = Native.DrawText(hdc, _statusText, -1, ref rect, DtWordBreak | DtCalcRect);
        Native.SelectObject(hdc, previous);
        Native.ReleaseDC(_status, hdc);
        return height;
    }

    /// <summary>ボタンを折り返して配置し、使った高さを返す。</summary>
    public int Layout(int width)
    {
        var scale = Scale;
        var height = (int)(24 * scale);
        var x = Margin;
        var y = Margin;
        for (var i = 0; i < _buttons.Count; i++)
        {
            var button = _buttons[i];
            var needed = button.Width;
            if (button.BreakBefore)
            {
                // まとまりの残りが同じ行に収まらなければ、まとまりごと次の行へ送る
                needed = _buttons.Skip(i).Sum(b => b.Width + Margin) - Margin;
                x += 6;
            }

            if (x + needed + Margin > width && x > Margin)
            {
                x = Margin;
                y += height + Margin;
            }

            Native.MoveWindow(button.Window, x, y, button.Width, height, 1);
            x += button.Width + Margin;
        }

        y += height + Margin;
        if (_statusText.Length == 0)
        {
            return y;
        }

        // 一時的な通知は、閉じるボタンの左に文字に必要な高さだけを使う
        var closeSize = (int)(20 * scale);
        var statusWidth = Math.Max(0, width - (Margin * 3) - closeSize);
        var statusHeight = Math.Max(MeasureStatus(statusWidth), closeSize);
        Native.MoveWindow(_status, Margin, y, statusWidth, statusHeight, 1);
        Native.MoveWindow(_closeStatus, width - Margin - closeSize, y, closeSize, closeSize, 1);
        return y + statusHeight + Margin;
    }
}
