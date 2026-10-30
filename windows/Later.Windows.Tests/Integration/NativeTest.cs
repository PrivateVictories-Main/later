using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
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
    [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] internal static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")] internal static partial int PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] internal static partial int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] internal static partial int PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Msg msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial nint DispatchMessage(in Msg msg);
    [LibraryImport("user32.dll")] internal static partial void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")] private static partial void SwitchToThisWindow(nint hwnd, int altTab);
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint hwnd, out WindowRect rect);
    [LibraryImport("user32.dll")] private static partial int SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")] private static partial void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [LibraryImport("user32.dll")] internal static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static partial uint GetWindowThreadProcessIdOut(nint hwnd, out uint processId);

    /// The process the foreground window belongs to (the app's own windows count as the app, whichever one the process calls its main).
    internal static uint ForegroundProcess() { _ = GetWindowThreadProcessIdOut(GetForegroundWindow(), out var pid); return pid; }
    [LibraryImport("user32.dll")] internal static partial int AttachThreadInput(uint from, uint to, int attach);

    internal const int SwMinimize = 6, SwRestore = 9;
    internal const uint WmClose = 0x0010, WmQuit = 0x0012;

    internal const byte VkControl = 0x11;

    /// Takes the keyboard for `hwnd` even when another window holds the foreground (a runner can have a first-run page up): Windows only lets
    /// the foreground's own thread hand it over, so the test joins that thread's input for the moment, with Alt tapped first as the usual nudge.
    internal static bool BringToFront(nint hwnd, uint processId)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (ForegroundProcess() == processId) return true;
            keybd_event(0x12 /*VK_MENU*/, 0, 0, 0);
            keybd_event(0x12, 0, 2, 0);
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), 0), mine = GetCurrentThreadId();
            bool joined = front != 0 && front != mine && AttachThreadInput(mine, front, 1) != 0;
            _ = ShowWindow(hwnd, SwRestore);
            _ = SetForegroundWindow(hwnd);
            if (joined) _ = AttachThreadInput(mine, front, 0);
            Thread.Sleep(200);
            if (ForegroundProcess() == processId) return true;
            // Last resort, which Windows always honours: a click is the user's own choice of window. It lands on the window's invisible
            // resize border, so it presses nothing of the app's.
            SwitchToThisWindow(hwnd, 1);
            if (attempt >= 1 && GetWindowRect(hwnd, out var r) != 0)
            {
                _ = SetCursorPos(r.Left + 3, (r.Top + r.Bottom) / 2);
                mouse_event(0x0002 /*LEFTDOWN*/, 0, 0, 0, 0);
                mouse_event(0x0004 /*LEFTUP*/, 0, 0, 0, 0);
                Thread.Sleep(300);
            }
        }
        return ForegroundProcess() == processId;
    }

    /// Presses a key (a virtual-key code; a letter's is its capital's character) and lets go, with Ctrl held when asked.
    internal static void Press(byte key, bool control = false)
    {
        if (control) keybd_event(VkControl, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, 2 /*KEYEVENTF_KEYUP*/, 0);
        if (control) keybd_event(VkControl, 0, 2, 0);
    }

    /// Types lowercase letters and digits.
    internal static void Type(string text)
    {
        foreach (var c in text) { Press((byte)char.ToUpperInvariant(c)); Thread.Sleep(30); }
    }

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

    public sealed record TabInfo(int Index, string Name, bool Selected, string ClassName = "", string AutomationId = "");

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
                if (UiaSession.Current(e, UiaIds.ControlType) is UiaIds.Document || UiaSession.Current(e, UiaIds.ClassName) as string != "Tab") continue;
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
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface((nint)unknown, in iid, out var pattern));
                int hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)pattern)[3])(pattern);   // vtable slot 3 = Select()
                _ = Marshal.Release(pattern); _ = unknown->Release();
                Marshal.ThrowExceptionForHR(hr);
            }
            finally { _ = tab->Release(); }
        }
        finally { if (found is not null) _ = found->Release(); _ = tabs->Release(); _ = root->Release(); }
    }

    /// Every descendant's name, in tree order: what assistive technology can read in the app's own window.
    public List<string> Names(nint hwnd)
    {
        var root = Root(hwnd);
        var all = session.True();
        IUIAutomationElementArray* found = null;
        var names = new List<string>();
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, all);
            for (int i = 0; i < found->Length; i++)
            {
                var element = found->GetElement(i);
                try { if (UiaSession.Current(element, UiaIds.Name) is string { Length: > 0 } name) names.Add(name); }
                finally { _ = element->Release(); }
            }
        }
        finally { if (found is not null) _ = found->Release(); _ = all->Release(); _ = root->Release(); }
        return names;
    }

    /// Which element has the keyboard: class, name and automation id.
    public string FocusedElement()
    {
        var element = session.Automation->GetFocusedElement();
        try { return $"{UiaSession.Current(element, UiaIds.ClassName)} | {UiaSession.Current(element, UiaIds.Name)} | {UiaSession.Current(element, UiaIds.AutomationId)}"; }
        finally { _ = element->Release(); }
    }

    /// Presses the focused element when it has this automation id: the runner's first-run privacy page keeps the keyboard until its Accept
    /// button is pressed (a pwsh step cannot see it). False when something else has the keyboard.
    public bool InvokeFocusedIf(string automationId)
    {
        var element = session.Automation->GetFocusedElement();
        try { return UiaSession.Current(element, UiaIds.AutomationId) as string == automationId && Call(element, 10000, new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), 3); }
        finally { _ = element->Release(); }
    }

    public const int ButtonType = 50000, ListItemType = 50007, MenuItemType = 50011;

    /// Names of the elements of a control type, in tree order (the list's rows come in the list's order).
    public List<string> NamesOf(nint hwnd, int controlType)
    {
        var result = new List<string>();
        var found = Find(hwnd, controlType, controlType);
        try { foreach (var p in found) if (UiaSession.Current((IUIAutomationElement*)p, UiaIds.Name) is string { Length: > 0 } name) result.Add(name); }
        finally { foreach (var p in found) _ = ((IUIAutomationElement*)p)->Release(); }
        return result;
    }

    /// The name of the element with this automation id, or null when there is none.
    public string? NameOfId(nint hwnd, string automationId)
    {
        var root = Root(hwnd);
        var id = session.StringIs(UiaIds.AutomationId, automationId);
        IUIAutomationElementArray* found = null;
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, id);
            if (found->Length == 0) return null;
            var element = found->GetElement(0);
            try { return UiaSession.Current(element, UiaIds.Name) as string; }
            finally { _ = element->Release(); }
        }
        finally { if (found is not null) _ = found->Release(); _ = id->Release(); _ = root->Release(); }
    }

    /// InvokePattern.Invoke on the first element of this control type and name. False when there is none (yet).
    public bool Invoke(nint hwnd, string name, int controlType = ButtonType) => Act(hwnd, name, controlType, 10000 /*Invoke*/, new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), 3);

    /// A menu item takes its click as Invoke, or, for the radio kind, as Select.
    public bool Choose(nint hwnd, string name, int controlType = MenuItemType) =>
        Act(hwnd, name, controlType, 10000 /*Invoke*/, new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), 3)
        || Act(hwnd, name, controlType, 10010 /*SelectionItem*/, new Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), 3);

    /// A menu's choice: the menu may be a window of its own, so the whole desktop's tree is searched, for this process's element of this name.
    /// Invoke, else Select, else Toggle: whichever the item supports.
    public bool ChooseInProcess(int processId, string name)
    {
        var root = session.Automation->GetRootElement();
        var byName = session.StringIs(UiaIds.Name, name);
        VARIANT pid = default;
        Marshal.GetNativeVariantForObject(processId, (nint)(&pid));
        var byProcess = session.Automation->CreatePropertyCondition((UIA_PROPERTY_ID)30002 /*ProcessId*/, pid);
        var both = session.Automation->CreateAndCondition(byName, byProcess);
        IUIAutomationElementArray* found = null;
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, both);
            for (int i = 0; i < found->Length; i++)
            {
                var element = found->GetElement(i);
                try
                {
                    if (Call(element, 10000, new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), 3)
                        || Call(element, 10010, new Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), 3)
                        || Call(element, 10015, new Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), 3)) return true;
                }
                finally { _ = element->Release(); }
            }
            return false;
        }
        finally { if (found is not null) _ = found->Release(); _ = both->Release(); _ = byProcess->Release(); _ = byName->Release(); _ = root->Release(); }
    }

    /// SelectionItemPattern.Select (or AddToSelection) on the first list row for which `which` holds.
    public bool SelectRow(nint hwnd, Func<string, bool> which, bool add = false)
    {
        var found = Find(hwnd, ListItemType, ListItemType);
        try
        {
            foreach (var p in found)
            {
                var element = (IUIAutomationElement*)p;
                if (UiaSession.Current(element, UiaIds.Name) is not string name || !which(name)) continue;
                return Call(element, 10010 /*SelectionItem*/, new Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), add ? 4 : 3);
            }
            return false;
        }
        finally { foreach (var p in found) _ = ((IUIAutomationElement*)p)->Release(); }
    }

    bool Act(nint hwnd, string name, int controlType, int pattern, Guid iid, int slot)
    {
        var root = Root(hwnd);
        var byName = session.StringIs(UiaIds.Name, name);
        var byType = session.ControlTypeIs(controlType);
        var both = session.Automation->CreateAndCondition(byName, byType);
        IUIAutomationElementArray* found = null;
        try
        {
            found = root->FindAll((TreeScope)UiaIds.ScopeDescendants, both);
            if (found->Length == 0) return false;
            var element = found->GetElement(0);
            try { return Call(element, pattern, iid, slot); }
            finally { _ = element->Release(); }
        }
        finally { if (found is not null) _ = found->Release(); _ = both->Release(); _ = byType->Release(); _ = byName->Release(); _ = root->Release(); }
    }

    /// Calls a pattern's method taking no arguments (vtable slot 3 and up).
    static bool Call(IUIAutomationElement* element, int pattern, Guid iid, int slot)
    {
        IUnknown* unknown = element->GetCurrentPattern((UIA_PATTERN_ID)pattern);
        if (unknown is null) return false;   // the element does not support the pattern
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface((nint)unknown, in iid, out var pointer));
        int hr = ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)pointer)[slot])(pointer);
        _ = Marshal.Release(pointer); _ = unknown->Release();
        Marshal.ThrowExceptionForHR(hr);
        return true;
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
