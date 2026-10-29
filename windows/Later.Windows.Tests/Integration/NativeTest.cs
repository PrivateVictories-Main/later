using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;
using Later.Windows.Native;

namespace Later.Windows.Tests.Integration;

[StructLayout(LayoutKind.Sequential)]
internal struct Msg { public nint Hwnd; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; }

/// Things only the tests do to a browser: switch tabs, minimize, close, pump messages. The product never does any of this.
internal static unsafe partial class NativeTest
{
    [LibraryImport("user32.dll")] internal static partial int ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")] internal static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] internal static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] internal static partial int PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] internal static partial int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] internal static partial int PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Msg msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(in Msg msg);
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();

    internal const int SwMinimize = 6, SwRestore = 9;
    internal const uint WmClose = 0x0010, WmQuit = 0x0012;

    /// Runs the calling thread's message loop until WM_QUIT.
    internal static void Pump()
    {
        while (GetMessage(out var m, 0, 0, 0) > 0) { _ = TranslateMessage(in m); _ = DispatchMessage(in m); }
    }
}

/// UI Automation from the test side: the live tab list, the selection action, and a tree dump for the probe.
internal sealed unsafe class UiaTest : IDisposable
{
    readonly UiaSession session = new();

    public record TabInfo(int Index, string Name, bool Selected, string ClassName = "", string AutomationId = "");

    /// Live tab items of a window in tree order.
    public List<TabInfo> Tabs(nint hwnd)
    {
        var result = new List<TabInfo>();
        var found = Find(hwnd, UiaIds.TabItem, UiaIds.Document);
        try
        {
            foreach (var p in found)
            {
                var e = (IUIAutomationElement*)p;
                if (UiaSession.Current(e, UiaIds.ControlType) is UiaIds.Document) continue;
                result.Add(new TabInfo(result.Count, UiaSession.Current(e, UiaIds.Name) as string ?? "", UiaSession.Current(e, UiaIds.IsSelected) is true,
                    UiaSession.Current(e, UiaIds.ClassName) as string ?? "", UiaSession.Current(e, UiaIds.AutomationId) as string ?? ""));
            }
        }
        finally { foreach (var p in found) _ = ((IUIAutomationElement*)p)->Release(); }
        return result;
    }

    /// SelectionItemPattern.Select on the nth tab. Test-only: the product never selects anything.
    public void SelectTab(nint hwnd, int index)
    {
        var root = Root(hwnd);
        IUIAutomationCondition* tabs = session.ControlTypeIs(UiaIds.TabItem);
        IUIAutomationElementArray* found = null;
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, tabs);
            IUIAutomationElement* tab = found->GetElement(index);
            try
            {
                IUnknown* unknown = tab->GetCurrentPattern((UIA_PATTERN_ID)10010 /*SelectionItem*/);
                var iid = new Guid("a8efa66a-0fda-421a-9194-38021f3578ea");   // IUIAutomationSelectionItemPattern
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface((nint)unknown, ref iid, out var pattern));
                int hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)pattern)[3])(pattern);   // vtable slot 3 = Select()
                _ = Marshal.Release(pattern); _ = unknown->Release();
                Marshal.ThrowExceptionForHR(hr);
            }
            finally { _ = tab->Release(); }
        }
        finally { if (found is not null) _ = found->Release(); _ = tabs->Release(); _ = root->Release(); }
    }

    /// A depth-limited look at the window's UI tree, skipping page content, one line per node.
    public List<string> Dump(nint hwnd, int maxDepth = 6, int maxNodes = 150)
    {
        var lines = new List<string>(); int nodes = 0;
        var root = Root(hwnd);
        IUIAutomationCondition* all = session.True();
        try
        {
            lines.Add($"root | {Describe(root)}");
            Walk(root, all, 1, maxDepth, ref nodes, maxNodes, lines);
        }
        finally { _ = all->Release(); _ = root->Release(); }
        return lines;
    }

    void Walk(IUIAutomationElement* parent, IUIAutomationCondition* all, int depth, int maxDepth, ref int nodes, int maxNodes, List<string> lines)
    {
        if (depth > maxDepth) return;
        IUIAutomationElementArray* kids;
        try { kids = parent->FindAll((TreeScope)UiaIds.ScopeChildren, all); }
        catch (COMException) { return; }
        int n = kids->Length;
        for (int i = 0; i < n && nodes < maxNodes; i++)
        {
            IUIAutomationElement* kid;
            try { kid = kids->GetElement(i); }
            catch (COMException) { continue; }
            nodes++;
            lines.Add($"{new string(' ', depth * 2)}{Describe(kid)}");
            if (UiaSession.Current(kid, UiaIds.ControlType) is not UiaIds.Document) Walk(kid, all, depth + 1, maxDepth, ref nodes, maxNodes, lines);
            _ = kid->Release();
        }
        _ = kids->Release();
    }

    static string Describe(IUIAutomationElement* e) =>
        $"{UiaSession.Current(e, UiaIds.ControlType)} | {UiaSession.Current(e, UiaIds.Name)} | {UiaSession.Current(e, UiaIds.AutomationId)} | {UiaSession.Current(e, UiaIds.ClassName)}";

    public string? RootName(nint hwnd)
    {
        var root = Root(hwnd);
        try { return UiaSession.Current(root, UiaIds.Name) as string; }
        finally { _ = root->Release(); }
    }

    IUIAutomationElement* Root(nint hwnd)
    {
        return session.Automation->ElementFromHandle(new HWND(hwnd));
    }

    /// Live elements of the given control types in tree order; the caller releases each.
    List<nint> Find(nint hwnd, int typeA, int typeB)
    {
        var root = Root(hwnd);
        var a = session.ControlTypeIs(typeA); var b = session.ControlTypeIs(typeB); var either = session.Or(a, b);
        IUIAutomationElementArray* found = null;
        var list = new List<nint>();
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, either);
            int n = found->Length;
            for (int i = 0; i < n; i++) list.Add((nint)found->GetElement(i));
        }
        finally { if (found is not null) _ = found->Release(); _ = either->Release(); _ = a->Release(); _ = b->Release(); _ = root->Release(); }
        return list;
    }

    public void Dispose() => session.Dispose();
}
