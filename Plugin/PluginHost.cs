using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MarkdownTrellis.Core;

namespace MarkdownTrellis.Plugin;

internal sealed record PluginCommand(string? Key, Action? Run);

internal sealed class DocumentState
{
    public SyntaxFeature Features;
    public bool StripOuterFence;
    public bool ForcePreview;
    public bool ManualUpdateRequested;
}

/// <summary>プラグイン全体の状態と、Notepad++・パネル・Coreの橋渡し。すべてUIスレッドで動かす。</summary>
internal static unsafe class PluginHost
{
    public const string ImageHost = "doc.markdowntrellis.example";

    private const string WindowClass = "MarkdownTrellisPanel";
    private const uint WmRenderDone = Native.WmApp + 1;
    private const uint WmJumpToSource = Native.WmApp + 2;
    private const uint WmJumpToHeading = Native.WmApp + 3;
    private const int SelectionWordLimitBytes = 200_000;
    private const nint RenderTimer = 1;
    private const nint CaretTimer = 2;
    private const nint LegacyTimer = 3;
    private const uint LegacyPollMs = 100;
    private const uint DebounceMs = 300;
    private const uint CaretMs = 150;
    private const int LargeDocumentBytes = 2 * 1024 * 1024;

    private static readonly string[] PreviewExtensions = [".md", ".markdown", ".mdown", ".mkd"];
    private static readonly Dictionary<nint, DocumentState> Documents = [];
    private static readonly List<GCHandle> Pins = [];

    private static nint _panel;
    private static WebView? _webView;
    private static LegacyPreview? _legacy;
    private static LegacyToolbar? _legacyToolbar;
    private static string? _webViewError;
    private static bool _panelVisible;
    private static bool _ready;
    private static bool _shellReady;
    private static int _renderSequence;
    private static int _displayedRenderSequence;
    private static nint _previewBuffer;
    private static nint _previewEditor;
    private static readonly Lock RenderGate = new();
    private static int _pendingSequence;
    private static string? _pendingRender;
    private static string? _pendingDialog;
    private static (nint Buffer, SyntaxFeature Feature, string Text)? _normalizeReview;
    private static int _lastCaretLine = -1;
    private static string? _lastSelectionKey;
    private static (nint Buffer, ClipboardImage Image)? _pendingImage;
    private static AppSettings _settings = new();
    private static string _language = "en";
    private static string _dataFolder = string.Empty;
    private static char* _title;
    private static bool _fallbackNoticeShown;

    private const int TitleLength = 128;

    public static NppData Npp;
    public static FuncItem* Items;

    public static IReadOnlyList<PluginCommand> Commands { get; } = CreateCommands();

    private static string SettingsPath => Path.Combine(_dataFolder, "settings.json");

    private static string Version
    {
        get
        {
            var version = typeof(PluginHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    public static string Text(string key) => Strings.Get(_language, key);

    public static void Initialize(NppData data)
    {
        Npp = data;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownTrellis");
        LoadSettings();
        _language = AppSettings.ResolveLanguage(_settings.Language, CultureInfo.CurrentUICulture);
    }

    public static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    public static void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            File.AppendAllText(Path.Combine(_dataFolder, "error.log"), $"{DateTime.Now:O} {exception}\r\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                _settings = AppSettings.FromJson(File.ReadAllText(SettingsPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log(new IOException("Failed to read settings: " + SettingsPath, ex));
            _settings = new AppSettings();
        }
    }

    private static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            File.WriteAllText(SettingsPath, AppSettings.ToJson(_settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log(new IOException("Failed to write settings: " + SettingsPath, ex));
        }
    }

    private static List<PluginCommand> CreateCommands()
    {
        PluginCommand Separator() => new(null, null);
        PluginCommand Inline(string key, InlineStyle style) => new(key, () => Format((t, s, e) => TextFormatter.ToggleInline(t, s, e, style)));
        PluginCommand Line(string key, LineStyle style) => new(key, () => Format((t, s, e) => TextFormatter.ToggleLineStyle(t, s, e, style), lineLevel: true));
        PluginCommand Heading(int level) => new("h" + level, () => Format((t, s, e) => TextFormatter.SetHeading(t, s, e, level), lineLevel: true));

        return
        [
            new("menu.panel", TogglePanel),
            Separator(),
            Inline("bold", InlineStyle.Bold),
            Inline("italic", InlineStyle.Italic),
            Inline("strike", InlineStyle.Strikethrough),
            Inline("code", InlineStyle.Code),
            Heading(1), Heading(2), Heading(3), Heading(4), Heading(5), Heading(6),
            Line("ul", LineStyle.BulletList),
            Line("ol", LineStyle.NumberedList),
            Line("task", LineStyle.TaskList),
            Line("quote", LineStyle.Quote),
            new("codeblock", () => OpenDialog("codeblock")),
            new("table", () => OpenDialog("table")),
            new("link", () => OpenDialog("link")),
            new("image", () => OpenDialog("image")),
            new("hr", () => Format((t, s, e) => TextFormatter.InsertHorizontalRule(t, s, e, Editor.Eol))),
            Separator(),
            new("copyMarkdown", CopyMarkdown),
            new("copyRich", CopyRich),
            Separator(),
            new("unwrap", UnwrapFence),
            new("ieEngine", ToggleIeEngine),
            new("about", ShowAbout),

            // Shortcut Mapperの割り当ては項目の順番で保存されるので、追加の項目は末尾に置く
            Separator(),
            new("copyRichSelection", CopyRichSelection),
            new("settings", () => OpenDialog("settings")),

            // 0.14.0（区切りを含めて37項目。Exports.cs の呼び出し口は40個）
            Separator(),
            new("headingUp", () => ShiftHeadings(-1)),
            new("headingDown", () => ShiftHeadings(1)),
            new("formatTable", FormatTable),
            new("pasteImage", PasteImage),
            new("copyHtml", CopyHtml),
        ];
    }

    // --- 通知 ---

    public static void OnNotification(ScNotification* notification)
    {
        var code = notification->Header.Code;
        switch (code)
        {
            case Native.NppnTbModification:
                if (Items != null)
                {
                    ToolbarIcon.Register(Npp.Npp, Items[0].CmdId);
                }

                break;
            case Native.NppnReady:
                _ready = true;
                UpdateMenuCheck();
                if (_settings.PanelVisible)
                {
                    ShowPanel();
                }

                break;
            case Native.NppnShutdown:
                _ready = false;
                _webView?.Close();
                break;
            case Native.NppnBufferActivated:
                _lastCaretLine = -1;

                // 前のタブの結果を表示しない。開いているダイアログは前のタブを対象にしていたので閉じる
                _renderSequence++;
                _normalizeReview = null;
                _pendingImage = null;
                _lastSelectionKey = null;
                if (_shellReady)
                {
                    _webView?.Post(Json(w => w.WriteString("type", "closeDialog")));
                }

                ScheduleRender(1);
                break;
            case Native.NppnFileClosed:
                Documents.Remove((nint)notification->Header.IdFrom);
                break;
            case Native.NppnFileSaved or Native.NppnFileRenamed or Native.NppnGlobalModified:
                ScheduleRender(DebounceMs);
                break;
            case Native.NppnDarkModeChanged:
                PostTheme();
                break;
            case Native.ScnModified:
                if (IsFromEditor(notification) && (notification->ModificationType & (Native.ScModInsertText | Native.ScModDeleteText)) != 0)
                {
                    // 選択の位置が同じでも中身が変わり得るので、次の通知で選択の数を送り直す
                    _lastSelectionKey = null;
                    ScheduleRender(DebounceMs);
                }

                break;
            case Native.ScnUpdateUi:
                if (_panelVisible && IsFromEditor(notification))
                {
                    Native.SetTimer(_panel, CaretTimer, CaretMs, 0);
                }

                break;
        }
    }

    private static bool IsFromEditor(ScNotification* notification) =>
        notification->Header.HwndFrom == Npp.Scintilla1 || notification->Header.HwndFrom == Npp.Scintilla2;

    private static void ScheduleRender(uint delay)
    {
        // 編集の300ms待ちの間も、古い表示から原文へ移動させない。
        _displayedRenderSequence = 0;
        _renderSequence++;
        if (_ready && _panelVisible && _panel != 0)
        {
            Native.SetTimer(_panel, RenderTimer, delay, 0);
        }
    }

    // --- パネル ---

    private static void TogglePanel()
    {
        if (_panelVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }

        _settings.PanelVisible = _panelVisible;
        SaveSettings();
    }

    private static void ShowPanel()
    {
        if (_panel == 0)
        {
            CreatePanel();
        }
        else
        {
            Native.SendMessage(Npp.Npp, Native.NppmDmmShow, 0, _panel);
        }

        _panelVisible = true;
        _webView?.SetVisible(true);
        UpdateMenuCheck();
        ScheduleRender(1);
    }

    private static void HidePanel()
    {
        Native.SendMessage(Npp.Npp, Native.NppmDmmHide, 0, _panel);
        _panelVisible = false;
        UpdateMenuCheck();
    }

    private static void UpdateMenuCheck()
    {
        if (Items != null)
        {
            Native.SendMessage(Npp.Npp, Native.NppmSetMenuItemCheck, Items[0].CmdId, _panelVisible ? 1 : 0);
            var engine = Commands.ToList().FindIndex(c => c.Key == "ieEngine");
            Native.SendMessage(Npp.Npp, Native.NppmSetMenuItemCheck, Items[engine].CmdId, UseIeEngine ? 1 : 0);
        }
    }

    private static bool UseIeEngine => _settings.PreviewEngine == "ie";

    /// <summary>WebView2を試さずにIEエンジンを使うかを切り替える。表示の作り直しは再起動時に行う。</summary>
    private static void ToggleIeEngine()
    {
        _settings.PreviewEngine = UseIeEngine ? "auto" : "ie";
        SaveSettings();
        UpdateMenuCheck();
        Native.MessageBox(Npp.Npp, Text(UseIeEngine ? "ieEngineOn" : "ieEngineOff"), "MarkdownTrellis", Native.MbIconInformation);
    }

    private static nint Pin(string text)
    {
        var handle = GCHandle.Alloc(text, GCHandleType.Pinned);
        Pins.Add(handle);
        return handle.AddrOfPinnedObject();
    }

    private static void CreatePanel()
    {
        var instance = Native.GetModuleHandle("MarkdownTrellis.dll");
        var wc = new WndClassEx
        {
            Size = (uint)sizeof(WndClassEx),
            WndProc = &PanelProc,
            Instance = instance,
            Background = 6,
            ClassName = Pin(WindowClass),
        };
        Native.RegisterClassEx(wc);
        _panel = Native.CreateWindowEx(0, WindowClass, "MarkdownTrellis", Native.WsChild | Native.WsClipChildren, 0, 0, 100, 100, Npp.Npp, 0, instance, 0);

        var data = (TbData*)NativeMemory.AllocZeroed((nuint)sizeof(TbData));
        data->Client = _panel;
        // タイトルは表示方式と言語で変わるので、書き換えられる領域を渡す
        _title = (char*)NativeMemory.AllocZeroed(TitleLength, sizeof(char));
        WriteTitle();
        data->Name = (nint)_title;
        data->DlgId = 0;
        data->Mask = Native.DwsDfContRight | Native.DwsUseOwnDarkMode;
        data->ModuleName = Pin("MarkdownTrellis.dll");
        Native.SendMessage(Npp.Npp, Native.NppmDmmRegAsDckDlg, 0, (nint)data);
        Native.SendMessage(Npp.Npp, Native.NppmDmmShow, 0, _panel);

        if (UseIeEngine)
        {
            StartLegacyPreview();
            return;
        }

        var shell = ReadShell();
        _webView = new WebView(_panel, shell, OnShellMessage, OpenExternalUrl, SaveCurrentFile, OnWebViewFailed);
        Directory.CreateDirectory(_dataFolder);
        try
        {
            _webView.Create(Path.Combine(_dataFolder, "WebView2"));
        }
        catch (Exception ex)
        {
            Log(ex);
            OnWebViewFailed(ex.Message);
        }
    }

    private static void WriteTitle()
    {
        var title = "MarkdownTrellis " + Version + (_legacy is not null ? Text("titleSimple") : string.Empty);
        var length = Math.Min(title.Length, TitleLength - 1);
        title.AsSpan(0, length).CopyTo(new Span<char>(_title, TitleLength));
        _title[length] = '\0';
    }

    /// <summary>実際に使っている表示方式と表示言語を、パネルのタイトルに反映する。</summary>
    private static void UpdatePanelTitle()
    {
        if (_title != null && _panel != 0)
        {
            WriteTitle();
            Native.SendMessage(Npp.Npp, Native.NppmDmmUpdateDispInfo, 0, _panel);
        }
    }

    private static string ReadShell()
    {
        using var stream = typeof(PluginHost).Assembly.GetManifestResourceStream("shell.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>IEエンジンの代替表示を始める。これも失敗したらパネルに案内だけを出す。</summary>
    private static void StartLegacyPreview()
    {
        try
        {
            var instance = Native.GetModuleHandle("MarkdownTrellis.dll");
            var preview = new LegacyPreview();
            if (preview.Create(_panel, instance))
            {
                _legacyToolbar = new LegacyToolbar(_panel);
                _legacyToolbar.Create(instance, Commands, Text);
                _legacy = preview;

                // 自動で切り替わったときだけ、起動中に一度短く知らせる。IEを選んだ利用者には出さない
                if (!UseIeEngine && !_fallbackNoticeShown)
                {
                    _fallbackNoticeShown = true;
                    _legacyToolbar.SetStatus(Text("legacyFallback"));
                }

                UpdatePanelTitle();
                LayoutLegacy();
                Native.SetTimer(_panel, LegacyTimer, LegacyPollMs, 0);
                ScheduleRender(1);
                return;
            }

            Log(new InvalidOperationException("IE preview could not be created."));
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        _webViewError = Text("webViewMissing");
        Native.PostMessage(_panel, Native.WmPaint, 0, 0);
    }

    private static void ShowFeaturesDialog()
    {
        var state = StateOf(CurrentBuffer);
        var fields = new List<DialogField> { new(DialogFieldKind.Label, Text("featuresHelp")) };
        foreach (var feature in Enum.GetValues<SyntaxFeature>().Where(f => f != SyntaxFeature.None))
        {
            var key = Strings.FeatureKey(feature);
            fields.Add(new(DialogFieldKind.Check, Text(key), isChecked: state.Features.HasFlag(feature), tag: feature.ToString()));
            fields.Add(new(DialogFieldKind.Label, Text(key + "Help"), indent: 12));
        }

        // 既定値への保存は、開くたびに未チェックから始める
        var saveDefault = new DialogField(DialogFieldKind.Check, Text("featuresSaveDefault"));
        fields.Add(saveDefault);
        bool IsFeature(DialogField field) => field.Kind == DialogFieldKind.Check && field != saveDefault;
        DialogButton[] extras =
        [
            new(Text("presetGitHub"), all =>
            {
                foreach (var field in all.Where(x => x.Tag is nameof(SyntaxFeature.Alerts) or nameof(SyntaxFeature.Footnotes)))
                {
                    field.Checked = true;
                }
            }),
            new(Text("clearAll"), all =>
            {
                foreach (var field in all.Where(IsFeature))
                {
                    field.Checked = false;
                }
            }),
        ];

        if (NativeDialog.Show(Npp.Npp, Text("features"), fields, "OK", Text("cancel"), extras, 260))
        {
            var selected = fields.Where(f => IsFeature(f) && f.Checked).Aggregate(SyntaxFeature.None, (sum, f) => sum | Enum.Parse<SyntaxFeature>(f.Tag));
            ApplyFeatures(state, selected, saveDefault.Checked);
        }
    }

    /// <summary>追加構文を現在のタブに適用する。指定があれば今後の文書の既定値にもする。</summary>
    private static void ApplyFeatures(DocumentState state, SyntaxFeature features, bool saveDefault)
    {
        state.Features = features;
        if (saveDefault)
        {
            _settings.DefaultFeatures = features;
            SaveSettings();
        }

        ScheduleRender(1);
    }

    /// <summary>WebView2を使えないときの設定画面。</summary>
    private static void ShowNativeSettings()
    {
        string[] languageValues = ["system", "ja", "en"];
        string[] languageLabels = [Text("languageSystem"), "日本語", "English"];
        // 想定外の値（手で編集した設定）は「Windowsに合わせる」として示す
        var current = Array.IndexOf(languageValues, _settings.Language) >= 0 ? _settings.Language : "system";
        var languages = languageValues.Select((value, i) => new DialogField(DialogFieldKind.Radio, languageLabels[i], isChecked: current == value, tag: "language")).ToList();
        var follow = new DialogField(DialogFieldKind.Check, Text("followCaret") + Text("followCaretLegacy"), isChecked: _settings.FollowCaret);
        var auto = new DialogField(DialogFieldKind.Radio, Text("engineAuto"), isChecked: !UseIeEngine, tag: "engine");
        var ie = new DialogField(DialogFieldKind.Radio, Text("engineIe"), isChecked: UseIeEngine, tag: "engine");
        var fields = new List<DialogField>
        {
            new(DialogFieldKind.Heading, Text("language")),
        };
        fields.AddRange(languages);
        fields.AddRange(
        [
            new(DialogFieldKind.Heading, Text("preview")), follow,
            new(DialogFieldKind.Heading, Text("engine")), auto, ie, new(DialogFieldKind.Label, Text("engineRestart"), indent: 12),
            new(DialogFieldKind.Heading, Text("simpleAbout")), new(DialogFieldKind.Label, Text("simpleAboutText").Replace("\n", "\r\n")),
            new(DialogFieldKind.Heading, string.Format(Text("versionLine"), Version)),
        ]);

        if (!NativeDialog.Show(Npp.Npp, Text("settingsTitle"), fields, "OK", Text("cancel"), null, 280))
        {
            return;
        }

        var language = languageValues[Math.Max(0, languages.FindIndex(f => f.Checked))];
        ApplySettings(language, follow.Checked, ie.Checked ? "ie" : "auto");
    }

    /// <summary>設定画面の［OK］の内容を反映する。</summary>
    private static void ApplySettings(string language, bool followCaret, string engine)
    {
        var resumeFollow = followCaret && !_settings.FollowCaret;
        _settings.FollowCaret = followCaret;

        // 想定外の値は auto と同じに働くので、auto を保存しても表示方式は変わらない（再起動の案内は出さない）
        var engineChanged = engine is "auto" or "ie" && (engine == "ie") != UseIeEngine;
        if (engine is "auto" or "ie" && _settings.PreviewEngine != engine)
        {
            _settings.PreviewEngine = engine;
            UpdateMenuCheck();
        }

        SaveSettings();
        if (language != _settings.Language)
        {
            SetLanguage(language);
        }

        if (resumeFollow)
        {
            // 追従を再開したら、すぐに現在の行へ合わせる
            _lastCaretLine = -1;
            PostCaret();
        }

        if (engineChanged)
        {
            PostToast(Text("engineChanged"));
        }
    }

    /// <summary>WebView2を使えないときの入力画面。</summary>
    private static void ShowNativeInsertDialog(string kind)
    {
        var selection = SelectedText();
        var title = Text(kind).TrimEnd('.');
        switch (kind)
        {
            case "table":
                DialogField columns = new(DialogFieldKind.Number, Text("cols"), "3");
                DialogField rows = new(DialogFieldKind.Number, Text("rows"), "2");
                if (NativeDialog.Show(Npp.Npp, title, [columns, rows], Text("insert"), Text("cancel")))
                {
                    var header = Text("headerFormat");
                    var c = int.TryParse(columns.Value, out var parsedColumns) ? parsedColumns : 3;
                    var r = int.TryParse(rows.Value, out var parsedRows) ? parsedRows : 2;
                    Format((t, s, e) => TextFormatter.InsertTable(t, s, e, c, r, header, Editor.Eol));
                }

                break;
            case "link":
            case "image":
                var isImage = kind == "image";
                DialogField label = new(DialogFieldKind.Text, Text(isImage ? "imageAlt" : "linkText"), selection);
                DialogField url = new(DialogFieldKind.Text, Text(isImage ? "imagePath" : "linkUrl"), isImage ? string.Empty : "https://");
                if (NativeDialog.Show(Npp.Npp, title, [label, url], Text("insert"), Text("cancel")))
                {
                    Format((t, s, e) => TextFormatter.InsertLink(t, s, e, label.Value, url.Value, isImage));
                }

                break;
            case "codeblock":
                DialogField language = new(DialogFieldKind.Text, Text("codeLanguage"));
                if (NativeDialog.Show(Npp.Npp, title, [language], Text("insert"), Text("cancel")))
                {
                    Format((t, s, e) => TextFormatter.InsertCodeBlock(t, s, e, language.Value, Editor.Eol));
                }

                break;
        }
    }

    private static void LayoutLegacy()
    {
        if (_legacy is not null && _legacyToolbar is not null && Native.GetClientRect(_panel, out var rect))
        {
            var top = _legacyToolbar.Layout(rect.Right);
            _legacy.Move(0, top, rect.Right, Math.Max(0, rect.Bottom - top));
        }
    }

    private static void OnWebViewFailed(string detail)
    {
        Log(new InvalidOperationException("WebView2: " + detail));
        _webView = null;
        StartLegacyPreview();
        if (_pendingDialog is { } pendingDialog)
        {
            _pendingDialog = null;
            OpenDialog(pendingDialog);
        }
    }

    [UnmanagedCallersOnly]
    private static nint PanelProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case Native.WmSize:
                    _webView?.Resize();
                    LayoutLegacy();
                    break;
                case Native.WmCommand:
                    var controlId = (int)(wParam & 0xFFFF);
                    if (controlId == LegacyToolbar.CloseStatusId)
                    {
                        _legacyToolbar?.SetStatus(string.Empty);
                        LayoutLegacy();
                        return 0;
                    }

                    if (controlId is LegacyToolbar.MoreButtonId or LegacyToolbar.SettingsButtonId && _legacyToolbar is not null)
                    {
                        var chosen = _legacyToolbar.ShowMenu(controlId, Commands, Text);
                        if (chosen == LegacyToolbar.FeaturesMenuId)
                        {
                            ShowFeaturesDialog();
                            return 0;
                        }

                        controlId = chosen;
                    }

                    var commandIndex = controlId - LegacyToolbar.FirstCommandId;
                    if (commandIndex >= 0 && commandIndex < Commands.Count && Commands[commandIndex].Run is { } run)
                    {
                        run();
                        return 0;
                    }

                    break;
                case Native.WmWindowPosChanged:
                    _webView?.NotifyMoved();
                    break;
                case Native.WmShowWindow:
                    _webView?.SetVisible(wParam != 0);
                    break;
                case Native.WmTimer:
                    if (wParam == LegacyTimer)
                    {
                        // 表示が終われば止め、次の SetBody で待ちが残ったときに再開する
                        _legacy?.Advance();
                        if (_legacy is not { IsWaiting: true })
                        {
                            Native.KillTimer(hwnd, LegacyTimer);
                        }

                        return 0;
                    }

                    Native.KillTimer(hwnd, wParam);
                    if (wParam == RenderTimer)
                    {
                        StartRender();
                    }
                    else if (wParam == CaretTimer)
                    {
                        PostCaret();
                    }

                    return 0;
                case WmRenderDone:
                    DeliverRender();
                    return 0;
                case WmJumpToSource:
                    JumpToSource((int)wParam, (int)lParam, grabFocus: true);
                    return 0;
                case WmJumpToHeading:
                    JumpToSource((int)wParam, (int)lParam, grabFocus: false);
                    return 0;
                case Native.WmNotify:
                    var header = (NotifyHeader*)lParam;
                    if ((header->Code & 0xFFFF) == Native.DmnClose)
                    {
                        _panelVisible = false;
                        _settings.PanelVisible = false;
                        SaveSettings();
                        UpdateMenuCheck();
                    }

                    break;
                case Native.WmPaint:
                    if (_webViewError is not null)
                    {
                        var hdc = Native.BeginPaint(hwnd, out var paint);
                        Native.GetClientRect(hwnd, out var rect);
                        rect.Left += 8;
                        rect.Top += 8;
                        Native.DrawText(hdc, _webViewError, -1, ref rect, 0x10);
                        Native.EndPaint(hwnd, paint);
                        return 0;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // --- プレビュー ---

    private static DocumentState StateOf(nint buffer)
    {
        if (!Documents.TryGetValue(buffer, out var state))
        {
            state = new DocumentState { Features = _settings.DefaultFeatures };
            Documents[buffer] = state;
        }

        return state;
    }

    private static nint CurrentBuffer => Native.SendMessage(Npp.Npp, Native.NppmGetCurrentBufferId, 0, 0);

    private static string CurrentPath()
    {
        var buffer = CurrentBuffer;
        var length = (int)Native.SendMessage(Npp.Npp, Native.NppmGetFullPathFromBufferId, buffer, 0);
        if (length <= 0)
        {
            return string.Empty;
        }

        var chars = new char[length + 1];
        fixed (char* data = chars)
        {
            Native.SendMessage(Npp.Npp, Native.NppmGetFullPathFromBufferId, buffer, (nint)data);
        }

        return new string(chars, 0, length);
    }

    private static bool IsPreviewTarget(string path, DocumentState state)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            // 無題の間にプレビューしていた文書は、別の拡張子で保存しても表示を続ける
            state.ForcePreview = true;
        }

        if (state.ForcePreview)
        {
            return true;
        }

        return Array.Exists(PreviewExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static void StartRender()
    {
        var legacy = _legacy is not null;
        if (!legacy && (!_shellReady || _webView is null))
        {
            return;
        }

        var sequence = ++_renderSequence;
        _displayedRenderSequence = 0;
        _previewBuffer = CurrentBuffer;
        _previewEditor = Editor.Current;
        var state = StateOf(CurrentBuffer);
        var path = CurrentPath();
        // 代替表示には「このファイルをプレビュー」のボタンがないので、すべての文書を表示する
        if (!legacy && !IsPreviewTarget(path, state))
        {
            PostStatus("notTarget", "previewThis", "forcePreview");
            return;
        }

        var size = Editor.GetLengthInBytes();
        if (size > LargeDocumentBytes && !state.ManualUpdateRequested)
        {
            PostStatus("tooLarge", "update", "update");
            return;
        }

        state.ManualUpdateRequested = false;
        var text = Editor.GetText();
        var folder = Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(path) : null;
        _webView?.SetImageFolder(folder);

        var features = state.Features;
        var strip = state.StripOuterFence;
        var language = _language;
        var extra = _settings.ExtraHtmlTags.ToArray();
        var host = folder is null ? null : ImageHost;

        Task.Run(() =>
        {
            string payload;
            try
            {
                var request = new RenderRequest(text, features, strip, extra, host);
                payload = legacy ? MarkdownRenderer.RenderLegacyPreview(request) : BuildRenderMessage(request, language, sequence);
            }
            catch (Exception ex)
            {
                Log(ex);
                payload = legacy
                    ? "<p>" + System.Net.WebUtility.HtmlEncode(Strings.Get(language, "renderFailed")) + "</p>"
                    : Json(w =>
                    {
                        w.WriteString("type", "status");
                        w.WriteString("text", Strings.Get(language, "renderFailed"));
                    });
            }

            // 完了順が前後しても、古い結果で新しい結果を上書きしない
            lock (RenderGate)
            {
                if (sequence > _pendingSequence)
                {
                    _pendingSequence = sequence;
                    _pendingRender = payload;
                }
            }

            Native.PostMessage(_panel, WmRenderDone, 0, 0);
        });
    }

    private static string BuildRenderMessage(RenderRequest request, string language, int sequence)
    {
        var result = MarkdownRenderer.RenderPreview(request);
        var fence = FenceDetector.Detect(request.Text);

        // プレビューの解析を共用し、全文をもう一度解析しない（外側フェンスを除外したときは位置が合わないので解析し直す）
        var detected = result.Document is { } document
            ? SyntaxDetector.Detect(document, request.Text, request.Features)
            : SyntaxDetector.Detect(request.Text, request.Features);
        var stats = TextStats.Count(request.Text);
        return Json(w =>
        {
            w.WriteString("type", "render");
            w.WriteNumber("sequence", sequence);
            w.WriteString("html", result.Html);
            w.WriteBoolean("isEmpty", request.Text.Length == 0);
            w.WriteStartObject("stats");
            w.WriteNumber("chars", stats.Characters);
            w.WriteNumber("lines", stats.Lines);
            w.WriteNumber("words", stats.Words);
            w.WriteEndObject();
            w.WriteStartArray("features");
            foreach (var feature in Enum.GetValues<SyntaxFeature>())
            {
                if (feature != SyntaxFeature.None && request.Features.HasFlag(feature))
                {
                    w.WriteStringValue(feature.ToString());
                }
            }

            w.WriteEndArray();
            w.WriteStartArray("notices");
            if (fence is not null)
            {
                w.WriteStartObject();
                w.WriteString("kind", "fence");
                w.WriteString("text", Strings.Get(language, fence.IsAmbiguous ? "noticeFenceAmbiguous" : "noticeFence"));
                w.WriteBoolean("stripped", request.StripOuterFence);
                w.WriteEndObject();
            }

            foreach (var item in detected)
            {
                var name = Strings.Get(language, Strings.FeatureKey(item.Feature));
                w.WriteStartObject();
                w.WriteString("kind", "syntax");
                w.WriteString("feature", item.Feature.ToString());
                w.WriteString("text", string.Format(Strings.Get(language, "noticeDetected"), name, item.Count));
                w.WriteBoolean("canConvert", Normalizer.Supported.Contains(item.Feature));
                w.WriteEndObject();
            }

            if (result.RejectedTags.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("kind", "info");
                w.WriteString("text", string.Format(Strings.Get(language, "noticeRejected"), string.Join(", ", result.RejectedTags.Select(t => $"<{t.Key}>×{t.Value}"))));
                w.WriteEndObject();
            }

            if (result.BlockedImages > 0)
            {
                w.WriteStartObject();
                w.WriteString("kind", "info");
                w.WriteString("text", string.Format(Strings.Get(language, "noticeImages"), result.BlockedImages));
                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }

    private static void DeliverRender()
    {
        string? payload;
        int sequence;
        lock (RenderGate)
        {
            payload = _pendingRender;
            sequence = _pendingSequence;
            _pendingRender = null;
        }

        // 表示の直前に、より新しい変換やタブ切替がなかったか確かめる
        if (payload is not null && sequence == _renderSequence && _legacy is not null)
        {
            SetLegacyBody(payload);
        }
        else if (payload is not null && sequence == _renderSequence)
        {
            _displayedRenderSequence = sequence;
            _webView?.Post(payload);
            PostCaret();
        }
    }

    /// <summary>
    /// カーソル行（追従する設定のとき、行が変わったときだけ）と、選択範囲の文字数（選択が変わったときだけ）をパネルへ送る。
    /// </summary>
    private static void PostCaret()
    {
        if (_webView is null)
        {
            return;
        }

        // 追従しない設定では、カーソルの移動でプレビューを動かさない
        int? line = null;
        if (_settings.FollowCaret)
        {
            var current = Editor.CurrentLine;
            if (current != _lastCaretLine)
            {
                _lastCaretLine = current;
                line = current;
            }
        }

        var selections = SelectionRanges();
        var key = string.Join(';', selections.Select(s => s.Start + "-" + s.End));
        var selectionChanged = key != _lastSelectionKey;
        _lastSelectionKey = key;
        if (line is null && !selectionChanged)
        {
            return;
        }

        TextCounts? counts = null;
        var tooLarge = false;
        if (selections.Count > 0)
        {
            var bytes = Editor.GetBytes();
            var encoding = Editor.CurrentEncoding;
            var total = selections.Sum(s => s.End - s.Start);
            tooLarge = total > SelectionWordLimitBytes;
            int chars = 0, lines = 0, words = 0;
            foreach (var (start, end) in selections)
            {
                var text = encoding.GetString(bytes, start, end - start);
                var part = tooLarge ? TextStats.Count(text) with { Words = 0 } : TextStats.Count(text);
                chars += part.Characters;
                lines += part.Lines;
                words += part.Words;
            }

            counts = new TextCounts(chars, lines, words);
        }

        _webView.Post(Json(w =>
        {
            w.WriteString("type", "caret");
            if (line is { } value)
            {
                w.WriteNumber("line", value);
            }

            if (selectionChanged)
            {
                if (counts is { } c)
                {
                    w.WriteStartObject("selection");
                    w.WriteNumber("chars", c.Characters);
                    w.WriteNumber("lines", c.Lines);
                    if (tooLarge)
                    {
                        w.WriteNull("words");
                    }
                    else
                    {
                        w.WriteNumber("words", c.Words);
                    }

                    w.WriteEndObject();
                }
                else
                {
                    w.WriteNull("selection");
                }
            }
        }));
    }

    /// <summary>空でない選択のバイト位置（複数選択・矩形選択は各選択）。</summary>
    private static List<(int Start, int End)> SelectionRanges()
    {
        var result = new List<(int, int)>();
        var count = (int)Editor.Send(Native.SciGetSelections);
        for (var i = 0; i < count; i++)
        {
            var start = (int)Editor.Send(Native.SciGetSelectionNStart, i);
            var end = (int)Editor.Send(Native.SciGetSelectionNEnd, i);
            if (end > start)
            {
                result.Add((start, end));
            }
        }

        return result;
    }

    /// <summary>プレビューからの要求が、表示中の最新の変換・同じ文書とビュー・有効な行を指しているか。</summary>
    private static bool IsCurrentPreviewLine(int sequence, int line) =>
        IsCurrentPreview(sequence) && line >= 0 && line < Editor.Send(Native.SciGetLineCount);

    /// <summary>プレビューからの要求が、表示中の最新の変換・同じ文書とビューのものか。</summary>
    private static bool IsCurrentPreview(int sequence) =>
        _legacy is null && _panelVisible && _displayedRenderSequence > 0 &&
        sequence == _displayedRenderSequence && sequence == _renderSequence &&
        _previewBuffer == CurrentBuffer && _previewEditor == Editor.Current;

    private static void JumpToSource(int sequence, int line, bool grabFocus)
    {
        if (!IsCurrentPreviewLine(sequence, line)) return;

        // この移動を原文→プレビューへ送り返さず、閲覧位置を維持する。
        _lastCaretLine = line;
        Editor.GoToLine(line, grabFocus);
    }

    /// <summary>プレビューのチェックボックスに対応する原文の行の [ ] と [x] を切り替える。</summary>
    private static void ToggleTask(int sequence, int line)
    {
        if (!IsCurrentPreviewLine(sequence, line))
        {
            return;
        }

        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return;
        }

        if (Editor.ToggleTask(line) == 2)
        {
            PostToast(Text("warnEncoding"));
        }
    }

    /// <summary>IEの表示に書く。読み込みの途中で書けなければ、タイマーで書けるまで待つ。</summary>
    private static void SetLegacyBody(string html)
    {
        _legacy!.SetBody(html);
        if (_legacy.IsWaiting)
        {
            Native.SetTimer(_panel, LegacyTimer, LegacyPollMs, 0);
        }
    }

    private static void PostStatus(string textKey, string actionKey, string command)
    {
        if (_legacy is not null)
        {
            SetLegacyBody("<p>" + System.Net.WebUtility.HtmlEncode(Text(textKey)) + "</p>");
        }

        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "status");
            w.WriteString("text", Text(textKey));
            w.WriteString("action", Text(actionKey));
            w.WriteString("command", command);
        }));
    }

    private static void PostToast(string text)
    {
        _legacyToolbar?.SetStatus(text);
        LayoutLegacy();
        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "toast");
            w.WriteString("text", text);
        }));
    }

    private static void PostTheme()
    {
        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "theme");
            w.WriteBoolean("dark", Native.SendMessage(Npp.Npp, Native.NppmIsDarkModeEnabled, 0, 0) != 0);
        }));
    }

    private static void PostInit()
    {
        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "init");
            w.WriteString("language", _language);
            w.WriteString("version", Version);
            w.WriteStartObject("strings");
            foreach (var item in Strings.All(_language))
            {
                w.WriteString(item.Key, item.Value);
            }

            w.WriteEndObject();
        }));
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // --- パネルからの操作 ---

    private static void OnShellMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var command = root.GetProperty("cmd").GetString();
        string Str(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        int Int(string name) => root.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
        var state = StateOf(CurrentBuffer);

        switch (command)
        {
            case "jumpToSource":
                if (root.TryGetProperty("sequence", out var sequenceValue) && sequenceValue.ValueKind == JsonValueKind.Number && sequenceValue.TryGetInt32(out var sequence) &&
                    root.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number && lineValue.TryGetInt32(out var line))
                {
                    // WebView2のコールバックを抜けてから移動し、ブラウザーにフォーカスを戻させない。
                    // 目次からの移動（focus: false）はフォーカスをパネルに残す。
                    var focus = !(root.TryGetProperty("focus", out var focusValue) && focusValue.ValueKind == JsonValueKind.False);
                    Native.PostMessage(_panel, focus ? WmJumpToSource : WmJumpToHeading, sequence, line);
                }

                break;
            case "toggleTask":
                if (root.TryGetProperty("sequence", out var taskSequenceValue) && taskSequenceValue.ValueKind == JsonValueKind.Number && taskSequenceValue.TryGetInt32(out var taskSequence) &&
                    root.TryGetProperty("line", out var taskLineValue) && taskLineValue.ValueKind == JsonValueKind.Number && taskLineValue.TryGetInt32(out var taskLine))
                {
                    ToggleTask(taskSequence, taskLine);
                }

                break;
            case "pasteImage":
                SavePastedImage(Str("name"), Str("alt"));
                break;
            case "ready":
                _shellReady = true;
                PostInit();
                PostTheme();
                ScheduleRender(1);
                if (_pendingDialog is { } pendingDialog)
                {
                    _pendingDialog = null;
                    OpenDialog(pendingDialog);
                }

                break;
            case "run":
                RunNamed(Str("name"));
                break;
            case "heading":
                var level = Int("level");
                Format((t, s, e) => TextFormatter.SetHeading(t, s, e, level), lineLevel: true);
                break;
            case "dialog":
                OpenDialog(Str("kind"));
                break;
            case "table":
                var columns = Int("cols");
                var rows = Int("rows");
                var header = Text("headerFormat");
                Format((t, s, e) => TextFormatter.InsertTable(t, s, e, columns, rows, header, Editor.Eol));
                break;
            case "link":
            case "image":
                var label = Str("text");
                var url = Str("url");
                var isImage = command == "image";
                Format((t, s, e) => TextFormatter.InsertLink(t, s, e, label, url, isImage));
                break;
            case "codeblock":
                var language = Str("lang");
                Format((t, s, e) => TextFormatter.InsertCodeBlock(t, s, e, language, Editor.Eol));
                break;
            case "applySettings":
                ApplySettings(Str("language"), root.GetProperty("followCaret").GetBoolean(), Str("engine"));
                break;
            case "setFeature":
                var feature = Enum.Parse<SyntaxFeature>(Str("feature"));
                state.Features = root.GetProperty("on").GetBoolean() ? state.Features | feature : state.Features & ~feature;
                ScheduleRender(1);
                break;
            case "setFeatures":
                var selected = SyntaxFeature.None;
                foreach (var item in root.GetProperty("features").EnumerateArray())
                {
                    selected |= Enum.Parse<SyntaxFeature>(item.GetString()!);
                }

                ApplyFeatures(state, selected, root.TryGetProperty("saveDefault", out var save) && save.GetBoolean());
                break;
            case "stripFence":
                state.StripOuterFence = root.GetProperty("on").GetBoolean();
                ScheduleRender(1);
                break;
            case "forcePreview":
                state.ForcePreview = true;
                ScheduleRender(1);
                break;
            case "update":
                state.ManualUpdateRequested = true;
                ScheduleRender(1);
                break;
            case "openLink":
                var linkSequence = root.TryGetProperty("sequence", out var linkSequenceValue) && linkSequenceValue.ValueKind == JsonValueKind.Number &&
                    linkSequenceValue.TryGetInt32(out var parsedSequence) ? parsedSequence : 0;
                OpenLink(Str("url"), linkSequence);
                break;
            case "normalizePreview":
                PostNormalizePreview(Enum.Parse<SyntaxFeature>(Str("feature")));
                break;
            case "normalizeApply":
                ApplyNormalize(Enum.Parse<SyntaxFeature>(Str("feature")));
                break;
            case "focusEditor":
                Editor.Send(Native.SciGrabFocus);
                break;
        }
    }

    private static void RunNamed(string name)
    {
        foreach (var item in Commands)
        {
            if (item.Key == name)
            {
                item.Run!();
                return;
            }
        }
    }

    private static void SetLanguage(string language)
    {
        if (language is not ("system" or "en" or "ja"))
        {
            return;
        }

        _settings.Language = language;
        SaveSettings();
        _language = AppSettings.ResolveLanguage(language, CultureInfo.CurrentUICulture);
        UpdateMenuLabels();
        UpdatePanelTitle();
        _legacyToolbar?.Relabel(Text);
        LayoutLegacy();
        PostInit();
        ScheduleRender(1);
    }

    private static void UpdateMenuLabels()
    {
        if (Items == null)
        {
            return;
        }

        var menu = Native.SendMessage(Npp.Npp, Native.NppmGetMenuHandle, 0, 0);
        var buffer = stackalloc char[256];
        for (var i = 0; i < Commands.Count; i++)
        {
            if (Commands[i].Key is not { } key)
            {
                continue;
            }

            // Shortcut Mapperで割り当てたキーの表示を保つ
            var id = (uint)Items[i].CmdId;
            var length = Native.GetMenuString(menu, id, buffer, 256, Native.MfByCommand);
            var current = length > 0 ? new string(buffer, 0, length) : string.Empty;
            var tab = current.IndexOf('\t');
            var label = Text(key) + (tab >= 0 ? current[tab..] : string.Empty);
            Native.ModifyMenu(menu, id, Native.MfByCommand | Native.MfString, id, label);
        }
    }

    /// <summary>外部のURL（http、https、mailto）だけを既定のアプリで開く。</summary>
    private static void OpenExternalUrl(string url)
    {
        if (HtmlSanitizer.IsExternalUrl(url))
        {
            Native.ShellExecute(Npp.Npp, "open", url, null, null, 1);
        }
    }

    /// <summary>プレビューのリンク。外部URLは既定のアプリで、相対リンクの .md は表示元の文書のフォルダで開く。</summary>
    private static void OpenLink(string url, int sequence)
    {
        if (HtmlSanitizer.IsExternalUrl(url))
        {
            OpenExternalUrl(url);
            return;
        }

        // 相対リンクは表示中の最新の変換・同じ文書とビューのときだけ解決する（タブ切替直後の古い表示から別のフォルダで開かない）
        if (!IsCurrentPreview(sequence))
        {
            return;
        }

        OpenLinkedMarkdown(url);
    }

    /// <summary>相対リンクが文書フォルダ配下のMarkdownファイルなら、Notepad++で開く。</summary>
    private static void OpenLinkedMarkdown(string href)
    {
        var path = CurrentPath();
        if (_legacy is not null || !Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } folder)
        {
            return;
        }

        var target = HtmlSanitizer.ResolveLinkPath(folder, href);
        if (target is null)
        {
            return;
        }

        if (!File.Exists(target))
        {
            PostToast(Text("linkNotFound"));
            return;
        }

        fixed (char* file = target)
        {
            Native.SendMessage(Npp.Npp, Native.NppmDoOpen, 0, (nint)file);
        }
    }

    private static void SaveCurrentFile() => Native.PostMessage(Npp.Npp, Native.WmCommand, Native.IdmFileSave, 0);

    // --- 編集 ---

    private static void Format(Func<string, int, int, TextEdit> action, bool lineLevel = false) =>
        TryFormat(action, out _, lineLevel);

    /// <summary>
    /// 書式の操作を適用する。読み取り専用・文字コードとして読めない文書では通知して false。
    /// edit は最後に適用した編集（どの選択も変えなければ null）。
    /// </summary>
    private static bool TryFormat(Func<string, int, int, TextEdit?> action, out TextEdit? edit, bool lineLevel = false, bool firstOnly = false)
    {
        edit = null;
        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return false;
        }

        if (!Editor.TryApply(action, out edit, lineLevel, firstOnly))
        {
            PostToast(Text("warnEncoding"));
            return false;
        }

        if (edit?.EmphasisIneffective == true)
        {
            PostToast(Text("warnEmphasis"));
        }

        return true;
    }

    /// <summary>見出しのレベルを1つ変える。選択がなければ文書全体、あれば選択した行。</summary>
    private static void ShiftHeadings(int delta)
    {
        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return;
        }

        // 空でない選択が覆う行の和集合（なければ文書全体）の見出しを、それぞれ1回だけ変える。空のカーソルは対象を決めない
        if (!Editor.TryApplyEdits((text, selections) => TextFormatter.ShiftHeadingEdits(text, selections, delta), out var count))
        {
            PostToast(Text("warnEncoding"));
            return;
        }

        if (count == 0)
        {
            PostToast(Text("headingNoChange"));
        }
    }

    /// <summary>カーソル（最初の選択）を含む表の列幅をそろえる。</summary>
    private static void FormatTable()
    {
        var found = false;
        var eol = Editor.Eol;
        Func<string, int, int, TextEdit?> action = (text, start, end) =>
        {
            var edit = TextFormatter.FormatTable(text, start, end, eol);
            found = edit is not null;
            return edit;
        };

        if (TryFormat(action, out _, firstOnly: true) && !found)
        {
            PostToast(Text("tableNotFound"));
        }
    }

    /// <summary>書式付きコピーと同じHTMLを、HTMLのソースの文字列としてコピーする。</summary>
    private static void CopyHtml()
    {
        var state = StateOf(CurrentBuffer);
        var request = new RenderRequest(Editor.GetText(), state.Features, state.StripOuterFence, _settings.ExtraHtmlTags);
        var result = MarkdownRenderer.RenderForCopy(request);
        var ok = Clipboard.Set(Npp.Npp, result.Html);
        PostToast(!ok ? Text("copyFailed") : result.LocalImages > 0 ? string.Format(Text("copiedHtmlImages"), result.LocalImages) : Text("copiedHtml"));
    }

    /// <summary>クリップボードの画像を文書フォルダの images へ保存して挿入する。保存の前に名前と代替テキストを確かめる。</summary>
    private static void PasteImage()
    {
        _pendingImage = null;
        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return;
        }

        if (!Editor.IsEncodingValid)
        {
            PostToast(Text("warnEncoding"));
            return;
        }

        var path = CurrentPath();
        if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } folder)
        {
            PostToast(Text("pasteSaveFirst"));
            return;
        }

        ClipboardImage? image;
        try
        {
            image = Clipboard.ReadImage(Npp.Npp);
        }
        catch (IOException)
        {
            PostToast(Text("copyFailed"));
            return;
        }

        if (image is null)
        {
            PostToast(Text("pasteNoImage"));
            return;
        }

        var images = Path.Combine(folder, PastedImage.FolderName);
        var name = PastedImage.MakeUnique(PastedImage.DefaultName(path, DateTime.Now), n => File.Exists(Path.Combine(images, n)));
        _pendingImage = (CurrentBuffer, image);

        if (_webViewError is null && _legacy is null)
        {
            if (!_panelVisible)
            {
                ShowPanel();
                _settings.PanelVisible = true;
                SaveSettings();
            }

            if (_webView is { IsReady: true } && _shellReady)
            {
                _webView.Post(Json(w =>
                {
                    w.WriteString("type", "dialog");
                    w.WriteString("kind", "pasteImage");
                    w.WriteString("name", name);
                    w.WriteString("text", string.Empty);
                }));
                _webView.Focus();
                return;
            }
        }

        DialogField nameField = new(DialogFieldKind.Text, Text("pasteFileName"), name);
        DialogField altField = new(DialogFieldKind.Text, Text("imageAlt"));
        if (NativeDialog.Show(Npp.Npp, Text("pasteImage").TrimEnd('.'), [nameField, altField], Text("insert"), Text("cancel"), null, 260))
        {
            SavePastedImage(nameField.Value, altField.Value);
        }
        else
        {
            _pendingImage = null;
        }
    }

    /// <summary>ダイアログで確定した名前で画像を保存し、カーソル位置に挿入する。</summary>
    private static void SavePastedImage(string requestedName, string alt)
    {
        var pending = _pendingImage;
        _pendingImage = null;
        if (pending is null || pending.Value.Buffer != CurrentBuffer)
        {
            return;
        }

        var path = CurrentPath();
        if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } folder)
        {
            PostToast(Text("pasteSaveFirst"));
            return;
        }

        if (PastedImage.Validate(requestedName) is not { } validName)
        {
            PostToast(Text("pasteBadName"));
            return;
        }

        // ダイアログを開いている間に読み取り専用・読めないバイトになっていれば、ファイルを作る前に中止する
        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return;
        }

        if (!Editor.IsEncodingValid)
        {
            PostToast(Text("warnEncoding"));
            return;
        }

        var images = Path.Combine(folder, PastedImage.FolderName);
        var root = Path.GetFullPath(folder);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(images, validName));
        string name;
        try
        {
            // 変換はファイルを作る前に済ませる（失敗しても文書フォルダに何も残さない）
            var png = PngWriter.Encode(pending.Value.Image);
            Directory.CreateDirectory(images);
            for (var attempt = 0; ; attempt++)
            {
                name = PastedImage.MakeUnique(validName, n => File.Exists(Path.Combine(images, n)));
                target = Path.GetFullPath(Path.Combine(images, name));
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    PostToast(Text("pasteBadName"));
                    return;
                }

                try
                {
                    PngWriter.WriteNew(target, png, root);
                    break;
                }
                catch (IOException ex) when (PngWriter.IsAlreadyExists(ex) && attempt < 20)
                {
                    // 確かめた後に同じ名前のファイルが作られた。既存のファイルは変えずに次の番号を試す
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log(new IOException("Failed to save the pasted image: " + target, ex));
            PostToast(string.Format(Text("pasteFailed"), target));
            return;
        }

        var url = PastedImage.RelativeUrl(name);
        if (!TryFormat((t, s, e) => TextFormatter.InsertLink(t, s, e, alt, url, isImage: true), out _))
        {
            // 挿入できなかった（通知済み）。今回作ったファイルだけを片付ける
            try
            {
                File.Delete(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(new IOException("Failed to remove the pasted image after the insertion failed: " + target, ex));
            }
        }
    }

    private static bool IsInCodeBlock()
    {
        var bytes = Editor.GetBytes();
        var encoding = Editor.CurrentEncoding;
        if (!Editor.RoundTrips(bytes, encoding))
        {
            // 位置を正しく求められない。続く書式の操作が中止して通知する
            return false;
        }

        var start = encoding.GetCharCount(bytes, 0, (int)Editor.Send(Native.SciGetSelectionNStart, 0));
        var end = encoding.GetCharCount(bytes, 0, (int)Editor.Send(Native.SciGetSelectionNEnd, 0));
        return TextFormatter.ToggleCodeFence(encoding.GetString(bytes), start, end, Editor.Eol) is not null;
    }

    private static void OpenDialog(string kind)
    {
        // 挿入のダイアログは、挿入できない文書（読み取り専用・読めないバイト）では開かずに知らせる
        if (kind is "codeblock" or "table" or "link" or "image")
        {
            if (Editor.IsReadOnly)
            {
                PostToast(Text("warnReadOnly"));
                return;
            }

            if (!Editor.IsEncodingValid)
            {
                PostToast(Text("warnEncoding"));
                return;
            }
        }

        if (kind == "codeblock" && IsInCodeBlock())
        {
            Format((t, s, e) => TextFormatter.ToggleCodeFence(t, s, e, Editor.Eol) ?? new TextEdit(s, 0, string.Empty, s, e), lineLevel: true);
            return;
        }

        if (_webViewError is null && _legacy is null)
        {
            // ダイアログはパネル内に出るので、隠れていれば先に開く
            if (!_panelVisible)
            {
                ShowPanel();
                _settings.PanelVisible = true;
                SaveSettings();
            }

            if (_webView is { IsReady: true } && _shellReady && kind == "settings")
            {
                PostSettings();
                _webView.Focus();
            }
            else if (_webView is { IsReady: true } && _shellReady)
            {
                var selection = SelectedText();
                _webView.Post(Json(w =>
                {
                    w.WriteString("type", "dialog");
                    w.WriteString("kind", kind);
                    w.WriteString("text", selection);
                }));
                _webView.Focus();
            }
            else
            {
                _pendingDialog = kind;
            }

            return;
        }

        if (kind == "settings")
        {
            ShowNativeSettings();
        }
        else
        {
            ShowNativeInsertDialog(kind);
        }
    }

    private static void PostSettings()
    {
        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "settings");
            w.WriteString("language", _settings.Language);
            w.WriteBoolean("followCaret", _settings.FollowCaret);
            w.WriteString("engine", UseIeEngine ? "ie" : "auto");
        }));
    }

    private static string SelectedText()
    {
        var start = (int)Editor.Send(Native.SciGetSelectionNStart, 0);
        var end = (int)Editor.Send(Native.SciGetSelectionNEnd, 0);
        if (start >= end)
        {
            return string.Empty;
        }

        var bytes = Editor.GetBytes();
        return Editor.CurrentEncoding.GetString(bytes, start, end - start);
    }

    private static void CopyMarkdown()
    {
        var ok = Clipboard.Set(Npp.Npp, Editor.GetText());
        PostToast(Text(ok ? "copiedMarkdown" : "copyFailed"));
    }

    private static void CopyRich()
    {
        var state = StateOf(CurrentBuffer);
        var request = new RenderRequest(Editor.GetText(), state.Features, state.StripOuterFence, _settings.ExtraHtmlTags);
        var result = MarkdownRenderer.RenderForCopy(request);
        var ok = Clipboard.Set(Npp.Npp, result.PlainText, RichHtmlConverter.BuildClipboardHtml(result.Html));
        PostToast(!ok ? Text("copyFailed") : result.LocalImages > 0 ? string.Format(Text("copiedRichImages"), result.LocalImages) : Text("copiedRich"));
    }

    /// <summary>
    /// 連続した1つの選択範囲を、単独のMarkdownとして書式付きでコピーする。
    /// 全文向けの外側フェンスの除外は使わず、原文と選択は変更しない。
    /// </summary>
    private static void CopyRichSelection()
    {
        if (Editor.Send(Native.SciSelectionIsRectangle) != 0 || Editor.Send(Native.SciGetSelections) > 1)
        {
            PostToast(Text("selectionMultiple"));
            return;
        }

        var fragment = SelectedText();
        if (fragment.Length == 0)
        {
            PostToast(Text("selectionNone"));
            return;
        }

        var state = StateOf(CurrentBuffer);
        var request = new RenderRequest(fragment, state.Features, false, _settings.ExtraHtmlTags);
        var result = MarkdownRenderer.RenderForCopy(request);
        var ok = Clipboard.Set(Npp.Npp, result.PlainText, RichHtmlConverter.BuildClipboardHtml(result.Html));
        if (!ok)
        {
            PostToast(Text("copyFailed"));
            return;
        }

        var message = result.LocalImages > 0 ? string.Format(Text("copiedRichSelectionImages"), result.LocalImages) : Text("copiedRichSelection");
        if (FragmentCheck.UsesOutsideDefinitions(Editor.GetText(), fragment, state.Features))
        {
            message += " " + Text("selectionOutside");
        }

        PostToast(message);
    }

    private static void UnwrapFence()
    {
        var text = Editor.GetText();
        var fence = FenceDetector.Detect(text);
        if (fence is null || Editor.IsReadOnly)
        {
            return;
        }

        if (!Editor.ReplaceAll(FenceDetector.Unwrap(text, fence)))
        {
            PostToast(Text("warnEncoding"));
            return;
        }

        StateOf(CurrentBuffer).StripOuterFence = false;
        ScheduleRender(1);
    }

    private static void PostNormalizePreview(SyntaxFeature feature)
    {
        var text = Editor.GetText();
        _normalizeReview = (CurrentBuffer, feature, text);
        var result = Normalizer.Apply(text, feature);
        _webView?.Post(Json(w =>
        {
            w.WriteString("type", "normalizePreview");
            w.WriteString("feature", feature.ToString());
            w.WriteBoolean("warn", result.MayLoseMeaning);
            w.WriteStartArray("changes");
            foreach (var change in result.Changes)
            {
                w.WriteStartObject();
                w.WriteNumber("line", change.Line);
                w.WriteString("before", change.Before);
                w.WriteString("after", change.After);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
    }

    private static void ApplyNormalize(SyntaxFeature feature)
    {
        if (Editor.IsReadOnly)
        {
            PostToast(Text("warnReadOnly"));
            return;
        }

        // 確認した差分と同じ文書の同じ内容にだけ適用する
        var text = Editor.GetText();
        var review = _normalizeReview;
        _normalizeReview = null;
        if (review is null || review.Value.Buffer != CurrentBuffer || review.Value.Feature != feature || review.Value.Text != text)
        {
            PostToast(Text("normalizeStale"));
            return;
        }

        var result = Normalizer.Apply(text, feature);
        if (result.Changes.Count > 0)
        {
            if (!Editor.ReplaceAll(result.Text))
            {
                PostToast(Text("warnEncoding"));
                return;
            }

            ScheduleRender(1);
        }
    }

    private static void ShowAbout() =>
        Native.MessageBox(Npp.Npp, $"MarkdownTrellis {Version}\r\n© ja1bus\r\n\r\nMIT License\r\nhttps://github.com/ja1bus/MarkdownTrellis", "MarkdownTrellis", Native.MbIconInformation);
}
