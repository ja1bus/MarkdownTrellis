using System.Runtime.InteropServices;
using MarkdownTrellis.Core;

namespace MarkdownTrellis.Plugin;

/// <summary>
/// WebView2のCOM呼び出し。NativeAOTでは組み込みのCOM相互運用を使えないため、
/// WebView2.h の順序に基づくvtable番号で直接呼び出す。
/// </summary>
internal sealed unsafe class WebView
{
    private const string ImageHostName = PluginHost.ImageHost;

    // 番号は WebView2.h（SDK 1.0.4314.35）の継承順に基づく（IUnknownの3個を含む）
    private const int EnvCreateController = 3;
    private const int CtlPutIsVisible = 4;
    private const int CtlPutBounds = 6;
    private const int CtlMoveFocus = 12;
    private const int CtlAddAcceleratorKeyPressed = 19;
    private const int CtlNotifyParentWindowPositionChanged = 23;
    private const int CtlClose = 24;
    private const int CtlGetCoreWebView2 = 25;
    private const int CoreGetSettings = 3;
    private const int CoreNavigateToString = 6;
    private const int CoreAddNavigationStarting = 7;
    private const int CoreAddFrameNavigationStarting = 17;
    private const int CorePostWebMessageAsJson = 32;
    private const int CoreAddWebMessageReceived = 34;
    private const int CoreAddNewWindowRequested = 44;
    private const int CoreAddWebResourceRequested = 55;
    private const int CoreAddWebResourceRequestedFilter = 57;
    private const int EnvCreateWebResourceResponse = 4;
    private const int ArgsGetRequest = 3;
    private const int ArgsPutResponse = 5;
    private const int RequestGetUri = 3;
    private const int ResourceContextImage = 3;
    private const int SetPutScriptDialogs = 8;
    private const int SetPutStatusBar = 10;
    private const int SetPutDevTools = 12;
    private const int SetPutHostObjects = 16;
    private const int SetPutBuiltInErrorPage = 20;
    private const int Set8PutReputationChecking = 36;
    private const int MsgTryGetString = 5;
    private const int NavGetUri = 3;
    private const int NavPutCancel = 8;
    private const int NewWinGetUri = 3;
    private const int NewWinPutHandled = 6;
    private const int NewWinGetUserInitiated = 8;
    private const int KeyGetKind = 3;
    private const int KeyGetVirtualKey = 4;
    private const int KeyPutHandled = 8;

    private static readonly Guid IidEnvCompleted = new("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d");
    private static readonly Guid IidCtlCompleted = new("6c4819f3-c9b7-4260-8127-c9f5bde7f68c");
    private static readonly Guid IidNavStarting = new("9adbe429-f36d-432b-9ddc-f8881fbd76e3");
    private static readonly Guid IidNewWindow = new("d4c185fe-c81c-4989-97af-2d3fa7ab5651");
    private static readonly Guid IidWebMessage = new("57213f19-00e6-49fa-8e07-898ea01ecbd2");
    private static readonly Guid IidAccelerator = new("b29c7e28-fa79-41a8-8e44-65811c76dcb2");
    private static readonly Guid IidWebResource = new("ab00b74c-15f1-4646-80e8-e76341d25d71");
    private static readonly Guid IidSettings8 = new("9e6b0e8f-86ad-4e81-8147-a9b5edb68650");

    private readonly nint _parent;
    private readonly string _shellHtml;
    private readonly Action<string> _onMessage;
    private readonly Action<string> _onOpenLink;
    private readonly Action _onSaveKey;
    private readonly Action<string> _onFailed;
    private nint _controller;
    private nint _core;
    private nint _environment;
    private bool _shellNavigated;
    private string? _imageFolder;

    public WebView(nint parent, string shellHtml, Action<string> onMessage, Action<string> onOpenLink, Action onSaveKey, Action<string> onFailed)
    {
        _parent = parent;
        _shellHtml = shellHtml;
        _onMessage = onMessage;
        _onOpenLink = onOpenLink;
        _onSaveKey = onSaveKey;
        _onFailed = onFailed;
    }

    public bool IsReady => _core != 0;

    public void Create(string userDataFolder)
    {
        var handler = ComCallback.Create(IidEnvCompleted, OnEnvironmentCreated);
        int hr;
        fixed (char* folder = userDataFolder)
        {
            hr = Native.CreateCoreWebView2EnvironmentWithOptions(null, folder, 0, handler);
        }

        if (hr < 0)
        {
            _onFailed($"CreateCoreWebView2Environment 0x{hr:X8}");
        }
    }

    private void OnEnvironmentCreated(nint result, nint environment)
    {
        if ((int)result < 0 || environment == 0)
        {
            _onFailed($"WebView2 environment 0x{(int)result:X8}");
            return;
        }

        Com.AddRef(environment);
        _environment = environment;
        var hr = Com.Call(environment, EnvCreateController, _parent, ComCallback.Create(IidCtlCompleted, OnControllerCreated));
        if (hr < 0)
        {
            _onFailed($"CreateCoreWebView2Controller 0x{hr:X8}");
        }
    }

    private void OnControllerCreated(nint result, nint controller)
    {
        if ((int)result < 0 || controller == 0)
        {
            _onFailed($"WebView2 controller 0x{(int)result:X8}");
            return;
        }

        Com.AddRef(controller);
        _controller = controller;
        Com.Check(Com.Get(controller, CtlGetCoreWebView2, out _core));
        Com.Check(Com.Get(_core, CoreGetSettings, out var settings));
        Com.Call(settings, SetPutScriptDialogs, 0);
        Com.Call(settings, SetPutStatusBar, 0);
        Com.Call(settings, SetPutDevTools, 0);
        Com.Call(settings, SetPutHostObjects, 0);
        Com.Call(settings, SetPutBuiltInErrorPage, 0);

        // 文書の内容がSmartScreenの評価へ送られないようにする
        if (Com.QueryInterface(settings, IidSettings8, out var settings8) >= 0)
        {
            Com.Call(settings8, Set8PutReputationChecking, 0);
            Com.Release(settings8);
        }

        Com.Release(settings);

        fixed (char* filter = "https://" + ImageHostName + "/*")
        {
            Com.Check(Com.Call(_core, CoreAddWebResourceRequestedFilter, (nint)filter, ResourceContextImage));
        }

        Register(_core, CoreAddWebResourceRequested, IidWebResource, OnWebResourceRequested);
        Register(_core, CoreAddNavigationStarting, IidNavStarting, OnNavigationStarting);
        Register(_core, CoreAddFrameNavigationStarting, IidNavStarting, OnFrameNavigationStarting);
        Register(_core, CoreAddNewWindowRequested, IidNewWindow, OnNewWindowRequested);
        Register(_core, CoreAddWebMessageReceived, IidWebMessage, OnWebMessage);
        Register(_controller, CtlAddAcceleratorKeyPressed, IidAccelerator, OnAcceleratorKey);

        Resize();
        fixed (char* html = _shellHtml)
        {
            Com.Check(Com.Call(_core, CoreNavigateToString, (nint)html));
        }
    }

    private static void Register(nint target, int slot, Guid iid, Action<nint, nint> handler)
    {
        long token;
        Com.Check(Com.Call(target, slot, ComCallback.Create(iid, handler), (nint)(&token)));
    }

    private void OnNavigationStarting(nint sender, nint args)
    {
        // シェルを最初に表示する1回だけ許可し、以後の移動はすべて止める
        if (!_shellNavigated)
        {
            _shellNavigated = true;
            return;
        }

        Com.Call(args, NavPutCancel, 1);
    }

    private void OnFrameNavigationStarting(nint sender, nint args)
    {
        var uri = Com.GetString(args, NavGetUri);
        if (uri is not ("about:srcdoc" or "about:blank"))
        {
            Com.Call(args, NavPutCancel, 1);
        }
    }

    private void OnNewWindowRequested(nint sender, nint args)
    {
        Com.Call(args, NewWinPutHandled, 1);
        Com.Check(Com.Get32(args, NewWinGetUserInitiated, out var userInitiated));
        var uri = Com.GetString(args, NewWinGetUri);
        if (userInitiated != 0 && uri is not null)
        {
            _onOpenLink(uri);
        }
    }

    private void OnWebMessage(nint sender, nint args)
    {
        var message = Com.GetString(args, MsgTryGetString);
        if (message is not null)
        {
            _onMessage(message);
        }
    }

    private void OnAcceleratorKey(nint sender, nint args)
    {
        Com.Get32(args, KeyGetKind, out var kind);
        Com.Get32(args, KeyGetVirtualKey, out var key);
        if (kind == 0 && key == 'S' && Native.GetKeyState(Native.VkControl) < 0)
        {
            Com.Call(args, KeyPutHandled, 1);
            _onSaveKey();
        }
    }

    public void Post(string json)
    {
        if (_core == 0)
        {
            return;
        }

        fixed (char* text = json)
        {
            Com.Call(_core, CorePostWebMessageAsJson, (nint)text);
        }
    }

    public void Resize()
    {
        if (_controller != 0 && Native.GetClientRect(_parent, out var rect))
        {
            Com.CallRect(_controller, CtlPutBounds, rect);
        }
    }

    /// <summary>ダイアログへ入力できるようキー入力をパネルへ移す。</summary>
    public void Focus()
    {
        if (_controller != 0)
        {
            Com.Call(_controller, CtlMoveFocus, 0);
        }
    }

    public void NotifyMoved()
    {
        if (_controller != 0)
        {
            Com.Call0(_controller, CtlNotifyParentWindowPositionChanged);
        }
    }

    public void SetVisible(bool visible)
    {
        if (_controller != 0)
        {
            Com.Call(_controller, CtlPutIsVisible, visible ? 1 : 0);
        }
    }

    /// <summary>プレビューから読み込める画像を、この文書のフォルダ配下に限定する。</summary>
    public void SetImageFolder(string? folder) => _imageFolder = folder;

    /// <summary>画像の要求に、文書フォルダ配下のファイルだけを返す。</summary>
    private void OnWebResourceRequested(nint sender, nint args)
    {
        Com.Check(Com.Get(args, ArgsGetRequest, out var request));
        var uri = Com.GetString(request, RequestGetUri);
        Com.Release(request);

        var path = uri is null || _imageFolder is null ? null : HtmlSanitizer.ResolveImagePath(_imageFolder, ImageHostName, uri);
        nint stream = 0;
        if (path is not null && Native.SHCreateStreamOnFileEx(path, Native.StgmRead | Native.StgmShareDenyNone, 0, 0, 0, out stream) < 0)
        {
            stream = 0;
        }

        var status = stream != 0 ? 200 : 404;
        var headers = stream != 0 ? "Content-Type: " + HtmlSanitizer.ImageContentType(path!) : string.Empty;
        fixed (char* reason = status == 200 ? "OK" : "Not Found")
        fixed (char* headerText = headers)
        {
            nint response = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, int, char*, char*, nint*, int>)Com.Slot(_environment, EnvCreateWebResourceResponse))(_environment, stream, status, reason, headerText, &response);
            if (hr >= 0)
            {
                Com.Call(args, ArgsPutResponse, response);
                Com.Release(response);
            }
        }

        if (stream != 0)
        {
            Com.Release(stream);
        }
    }

    public void Close()
    {
        if (_controller != 0)
        {
            Com.Call0(_controller, CtlClose);
            _controller = 0;
            _core = 0;
        }
    }
}

internal static unsafe class Com
{
    public static int QueryInterface(nint o, Guid iid, out nint result)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(o, 0))(o, &iid, &value);
        result = value;
        return hr;
    }

    public static void AddRef(nint o) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(o, 1))(o);

    public static void Release(nint o) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(o, 2))(o);

    public static int Call0(nint o, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(o, slot))(o);

    public static int Call(nint o, int slot, nint a) => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(o, slot))(o, a);

    public static int Call(nint o, int slot, nint a, nint b) => ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(o, slot))(o, a, b);

    public static int CallRect(nint o, int slot, Rect rect) => ((delegate* unmanaged[Stdcall]<nint, Rect, int>)Slot(o, slot))(o, rect);

    public static int Get(nint o, int slot, out nint value)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(o, slot))(o, &result);
        value = result;
        return hr;
    }

    public static int Get32(nint o, int slot, out int value)
    {
        var result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(o, slot))(o, &result);
        value = result;
        return hr;
    }

    public static string? GetString(nint o, int slot)
    {
        if (Get(o, slot, out var text) < 0 || text == 0)
        {
            return null;
        }

        var value = Marshal.PtrToStringUni(text);
        Native.CoTaskMemFree(text);
        return value;
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    public static nint Slot(nint o, int slot) => (*(nint**)o)[slot];
}

/// <summary>WebView2へ渡すコールバックのCOMオブジェクト（IUnknownとInvokeのみ）。</summary>
internal static unsafe class ComCallback
{
    private static readonly nint* Vtable = CreateVtable();

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public nint* Vtable;
        public int Refs;
        public Guid Iid;
        public nint Handle;
    }

    /// <summary>Invokeの引数（完了通知ならHRESULTと結果、イベントなら送信元と引数）をそのまま渡す。プロセス終了まで保持する。</summary>
    public static nint Create(Guid iid, Action<nint, nint> handler)
    {
        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));
        instance->Vtable = Vtable;
        instance->Refs = 1;
        instance->Iid = iid;
        instance->Handle = GCHandle.ToIntPtr(GCHandle.Alloc(handler));
        return (nint)instance;
    }

    private static nint* CreateVtable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(sizeof(nint) * 4));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Invoke;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        var instance = (Instance*)self;
        if (*iid == instance->Iid || *iid == new Guid("00000000-0000-0000-C000-000000000046"))
        {
            instance->Refs++;
            *result = self;
            return 0;
        }

        *result = 0;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)++((Instance*)self)->Refs;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint Release(nint self) => (uint)--((Instance*)self)->Refs;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int Invoke(nint self, nint a, nint b)
    {
        try
        {
            var handler = (Action<nint, nint>)GCHandle.FromIntPtr(((Instance*)self)->Handle).Target!;
            handler(a, b);
        }
        catch (Exception ex)
        {
            PluginHost.Log(ex);
        }

        return 0;
    }
}
