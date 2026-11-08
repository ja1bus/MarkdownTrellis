using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MarkdownTrellis.Plugin;

/// <summary>Notepad++が呼び出す関数。例外をNotepad++へ伝えない。</summary>
internal static unsafe class Exports
{
    private static readonly delegate* unmanaged<void>[] Thunks =
    [
        &C0, &C1, &C2, &C3, &C4, &C5, &C6, &C7, &C8, &C9, &C10, &C11, &C12, &C13, &C14, &C15,
        &C16, &C17, &C18, &C19, &C20, &C21, &C22, &C23, &C24, &C25, &C26, &C27, &C28, &C29, &C30, &C31, &C32, &C33, &C34, &C35,
    ];

    private static FuncItem* _items;
    private static char* _name;

    [UnmanagedCallersOnly(EntryPoint = "isUnicode", CallConvs = [typeof(CallConvCdecl)])]
    public static int IsUnicode() => 1;

    [UnmanagedCallersOnly(EntryPoint = "setInfo", CallConvs = [typeof(CallConvCdecl)])]
    public static void SetInfo(NppData data) => PluginHost.Safe(() => PluginHost.Initialize(data));

    [UnmanagedCallersOnly(EntryPoint = "getName", CallConvs = [typeof(CallConvCdecl)])]
    public static char* GetName()
    {
        if (_name == null)
        {
            _name = (char*)Marshal.StringToHGlobalUni("MarkdownTrellis");
        }

        return _name;
    }

    [UnmanagedCallersOnly(EntryPoint = "getFuncsArray", CallConvs = [typeof(CallConvCdecl)])]
    public static FuncItem* GetFuncsArray(int* count)
    {
        try
        {
            var commands = PluginHost.Commands;
            if (commands.Count > Thunks.Length)
            {
                throw new InvalidOperationException($"Add thunks: {commands.Count} commands, {Thunks.Length} thunks.");
            }

            if (_items == null)
            {
                _items = (FuncItem*)NativeMemory.AllocZeroed((nuint)(sizeof(FuncItem) * commands.Count));
                for (var i = 0; i < commands.Count; i++)
                {
                    var label = commands[i].Key is null ? string.Empty : PluginHost.Text(commands[i].Key!);
                    label.AsSpan(0, Math.Min(label.Length, 63)).CopyTo(new Span<char>(_items[i].ItemName, 64));
                    _items[i].Func = commands[i].Key is null ? null : Thunks[i];
                }
            }

            *count = commands.Count;
            PluginHost.Items = _items;
            return _items;
        }
        catch (Exception ex)
        {
            PluginHost.Log(ex);
            *count = 0;
            return null;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "beNotified", CallConvs = [typeof(CallConvCdecl)])]
    public static void BeNotified(ScNotification* notification)
    {
        try
        {
            PluginHost.OnNotification(notification);
        }
        catch (Exception ex)
        {
            PluginHost.Log(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "messageProc", CallConvs = [typeof(CallConvCdecl)])]
    public static nint MessageProc(uint message, nint wParam, nint lParam) => 1;

    private static void Run(int index) => PluginHost.Safe(PluginHost.Commands[index].Run!);

    [UnmanagedCallersOnly] private static void C0() => Run(0);
    [UnmanagedCallersOnly] private static void C1() => Run(1);
    [UnmanagedCallersOnly] private static void C2() => Run(2);
    [UnmanagedCallersOnly] private static void C3() => Run(3);
    [UnmanagedCallersOnly] private static void C4() => Run(4);
    [UnmanagedCallersOnly] private static void C5() => Run(5);
    [UnmanagedCallersOnly] private static void C6() => Run(6);
    [UnmanagedCallersOnly] private static void C7() => Run(7);
    [UnmanagedCallersOnly] private static void C8() => Run(8);
    [UnmanagedCallersOnly] private static void C9() => Run(9);
    [UnmanagedCallersOnly] private static void C10() => Run(10);
    [UnmanagedCallersOnly] private static void C11() => Run(11);
    [UnmanagedCallersOnly] private static void C12() => Run(12);
    [UnmanagedCallersOnly] private static void C13() => Run(13);
    [UnmanagedCallersOnly] private static void C14() => Run(14);
    [UnmanagedCallersOnly] private static void C15() => Run(15);
    [UnmanagedCallersOnly] private static void C16() => Run(16);
    [UnmanagedCallersOnly] private static void C17() => Run(17);
    [UnmanagedCallersOnly] private static void C18() => Run(18);
    [UnmanagedCallersOnly] private static void C19() => Run(19);
    [UnmanagedCallersOnly] private static void C20() => Run(20);
    [UnmanagedCallersOnly] private static void C21() => Run(21);
    [UnmanagedCallersOnly] private static void C22() => Run(22);
    [UnmanagedCallersOnly] private static void C23() => Run(23);
    [UnmanagedCallersOnly] private static void C24() => Run(24);
    [UnmanagedCallersOnly] private static void C25() => Run(25);
    [UnmanagedCallersOnly] private static void C26() => Run(26);
    [UnmanagedCallersOnly] private static void C27() => Run(27);
    [UnmanagedCallersOnly] private static void C28() => Run(28);
    [UnmanagedCallersOnly] private static void C29() => Run(29);
    [UnmanagedCallersOnly] private static void C30() => Run(30);
    [UnmanagedCallersOnly] private static void C31() => Run(31);
    [UnmanagedCallersOnly] private static void C32() => Run(32);
    [UnmanagedCallersOnly] private static void C33() => Run(33);
    [UnmanagedCallersOnly] private static void C34() => Run(34);
    [UnmanagedCallersOnly] private static void C35() => Run(35);
}
