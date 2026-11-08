using System.Runtime.InteropServices;

namespace MarkdownTrellis.Plugin;

[StructLayout(LayoutKind.Sequential)]
internal struct NppData
{
    public nint Npp;
    public nint Scintilla1;
    public nint Scintilla2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FuncItem
{
    public fixed char ItemName[64];
    public delegate* unmanaged<void> Func;
    public int CmdId;
    public byte Init2Check;
    public nint ShortcutKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TbData
{
    public nint Client;
    public nint Name;
    public int DlgId;
    public uint Mask;
    public nint IconTab;
    public nint AddInfo;
    public Rect RcFloat;
    public int PrevCont;
    public nint ModuleName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NotifyHeader
{
    public nint HwndFrom;
    public nuint IdFrom;
    public uint Code;
}

/// <summary>SCNotificationの先頭部分（使う項目まで）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ScNotification
{
    public NotifyHeader Header;
    public nint Position;
    public int Ch;
    public int Modifiers;
    public int ModificationType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WndClassEx
{
    public uint Size;
    public uint Style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
    public int ClsExtra;
    public int WndExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public nint MenuName;
    public nint ClassName;
    public nint IconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PaintStruct
{
    public nint Hdc;
    public int Erase;
    public Rect RcPaint;
    public int Restore;
    public int IncUpdate;
    public fixed byte Reserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct ToolbarIcons
{
    public nint Bitmap;
    public nint Icon;
    public nint IconDark;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IconInfo
{
    public int IsIcon;
    public uint HotspotX;
    public uint HotspotY;
    public nint Mask;
    public nint Color;
}

internal static partial class Native
{
    public const uint WmUser = 0x400;
    public const uint WmApp = 0x8000;
    public const uint WmSize = 0x0005;
    public const uint WmPaint = 0x000F;
    public const uint WmNotify = 0x004E;
    public const uint WmTimer = 0x0113;
    public const uint WmCommand = 0x0111;
    public const uint WmWindowPosChanged = 0x0047;
    public const uint WmShowWindow = 0x0018;

    // Notepad++ (NPPMSG = WM_USER + 1000)
    public const uint NppMsg = WmUser + 1000;
    public const uint NppmGetCurrentScintilla = NppMsg + 4;
    public const uint NppmGetMenuHandle = NppMsg + 25;
    public const uint NppmDmmShow = NppMsg + 30;
    public const uint NppmDmmHide = NppMsg + 31;
    public const uint NppmDmmUpdateDispInfo = NppMsg + 32;
    public const uint NppmDmmRegAsDckDlg = NppMsg + 33;
    public const uint NppmSetMenuItemCheck = NppMsg + 40;
    public const uint NppmGetFullPathFromBufferId = NppMsg + 58;
    public const uint NppmGetCurrentBufferId = NppMsg + 60;
    public const uint NppmIsDarkModeEnabled = NppMsg + 107;

    public const uint NppmAddToolbarIconForDarkMode = NppMsg + 101;

    public const uint NppnReady = 1001;
    public const uint NppnTbModification = 1002;
    public const uint NppnFileClosed = 1005;
    public const uint NppnFileSaved = 1008;
    public const uint NppnShutdown = 1009;
    public const uint NppnBufferActivated = 1010;
    public const uint NppnFileRenamed = 1023;
    public const uint NppnDarkModeChanged = 1027;
    public const uint NppnGlobalModified = 1030;

    public const uint DmnClose = 1051;
    public const int IdmFileSave = 41006;
    public const uint DwsDfContRight = 1u << 28;
    public const uint DwsUseOwnDarkMode = 8;

    // Scintilla
    public const uint SciGetLength = 2006;
    public const uint SciGetCurrentPos = 2008;
    public const uint SciGetEolMode = 2030;
    public const uint SciBeginUndoAction = 2078;
    public const uint SciEndUndoAction = 2079;
    public const uint SciGetCodePage = 2137;
    public const uint SciGetReadOnly = 2140;
    public const uint SciSetSel = 2160;
    public const uint SciLineFromPosition = 2166;
    public const uint SciSetTargetStart = 2190;
    public const uint SciSetTargetEnd = 2192;
    public const uint SciReplaceTarget = 2194;
    public const uint SciSelectionIsRectangle = 2372;
    public const uint SciGrabFocus = 2400;
    public const uint SciGetCharacterPointer = 2520;
    public const uint SciGetSelections = 2570;
    public const uint SciGetSelectionNStart = 2585;
    public const uint SciGetSelectionNEnd = 2587;
    public const uint ScnUpdateUi = 2007;
    public const uint ScnModified = 2008;
    public const int ScModInsertText = 1;
    public const int ScModDeleteText = 2;

    public const uint WsChild = 0x40000000;
    public const uint WsClipChildren = 0x02000000;
    public const uint MbIconInformation = 0x40;
    public const uint MfByCommand = 0;
    public const uint MfString = 0;
    public const uint MfSeparator = 0x800;
    public const uint TpmReturnCmd = 0x100;
    public const uint GmemMoveable = 2;
    public const uint CfUnicodeText = 13;
    public const int VkControl = 0x11;

    [LibraryImport("user32", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(in WndClassEx wc);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32")]
    public static partial nint SetTimer(nint hwnd, nint id, uint ms, nint proc);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nint id);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    [LibraryImport("user32")]
    public static partial nint BeginPaint(nint hwnd, out PaintStruct ps);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, in PaintStruct ps);

    [LibraryImport("user32", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint hdc, string text, int length, ref Rect rect, uint format);

    [LibraryImport("user32")]
    public static partial short GetKeyState(int key);

    [LibraryImport("user32", EntryPoint = "ModifyMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ModifyMenu(nint menu, uint position, uint flags, nuint id, string text);

    [LibraryImport("user32")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32")]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32", EntryPoint = "GetMenuStringW")]
    public static unsafe partial int GetMenuString(nint menu, uint id, char* buffer, int max, uint flags);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32")]
    public static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32")]
    public static partial nint GlobalAlloc(uint flags, nuint size);

    [LibraryImport("kernel32")]
    public static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint handle);

    [LibraryImport("kernel32")]
    public static partial nint GlobalFree(nint handle);

    [LibraryImport("kernel32")]
    public static partial uint GetACP();

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string name);

    [LibraryImport("shell32", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);

    [LibraryImport("ole32")]
    public static partial void CoTaskMemFree(nint ptr);

    public const uint WsVisible = 0x10000000;

    [LibraryImport("user32", EntryPoint = "DialogBoxIndirectParamW")]
    public static unsafe partial nint DialogBoxIndirectParam(nint instance, byte* template, nint owner, delegate* unmanaged<nint, uint, nint, nint, nint> proc, nint param);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDialog(nint dialog, nint result);

    [LibraryImport("user32")]
    public static partial nint GetDlgItem(nint dialog, int id);

    [LibraryImport("user32", EntryPoint = "GetWindowTextW")]
    public static unsafe partial int GetWindowText(nint window, char* buffer, int max);

    [LibraryImport("user32", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageText(nint window, uint message, nint wParam, string text);

    [LibraryImport("user32")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32")]
    public static partial int ReleaseDC(nint window, nint hdc);

    [LibraryImport("gdi32")]
    public static partial nint SelectObject(nint hdc, nint handle);

    [LibraryImport("atl")]
    public static partial int AtlAxWinInit();

    [LibraryImport("atl")]
    public static partial int AtlAxGetControl(nint window, out nint control);

    [LibraryImport("shlwapi")]
    public static unsafe partial nint SHCreateMemStream(byte* data, uint size);

    [LibraryImport("user32")]
    public static partial int MoveWindow(nint window, int x, int y, int width, int height, int repaint);

    [LibraryImport("user32")]
    public static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetWindowText(nint window, string text);

    [LibraryImport("gdi32")]
    public static partial nint GetStockObject(int index);

    [LibraryImport("gdi32")]
    public static unsafe partial nint CreateDIBSection(nint hdc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32")]
    public static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, nint bits);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint handle);

    [LibraryImport("user32")]
    public static unsafe partial nint CreateIconIndirect(IconInfo* info);

    public const uint StgmRead = 0;
    public const uint StgmShareDenyNone = 0x40;

    [LibraryImport("shlwapi", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, int create, nint template, out nint stream);

    [DllImport("WebView2Loader", ExactSpelling = true)]
    public static extern unsafe int CreateCoreWebView2EnvironmentWithOptions(char* browserFolder, char* userDataFolder, nint options, nint handler);
}
