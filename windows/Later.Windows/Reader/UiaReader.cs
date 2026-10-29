using Later.Core;
using Later.Windows.Native;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Later.Windows.Reader;

/// One bounded UI Automation pass over a browser window. Reads only (properties cached per call); never registers an event
/// handler, never invokes a pattern, never touches the window. MTA thread.
///
/// The order is the privacy rule: the window is classified first from its root view's name alone, and nothing inside the
/// window is read unless it is positively a normal one. Then the browser's own UI is walked breadth-limited (the tab
/// strip), without ever entering page content; the only page element asked for is the first Document, for its address.
internal sealed unsafe class UiaReader : IDisposable
{
    readonly UiaSession session = new();
    readonly Func<Browser, string> dataFolder;
    readonly Dictionary<string, ProfileNames> profileNames = [];
    IUIAutomationCacheRequest* cache;
    IUIAutomationCondition* rootViewIs, documentIs, everything;

    const string TabClass = "Tab", RootViewClass = "BrowserRootView";
    const int MaxDepth = 10, MaxQueries = 60, ClassifyAttempts = 3;
    // Page content and the parts of the browser's chrome that never hold tabs: not entered.
    static readonly HashSet<string> NotEntered = new(StringComparer.Ordinal)
    {
        "MultiContentsView", "WebView", "Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "InfoBarContainerView", "BrowserCaptionButtonContainer", "ChromeBorder", "FrameGrabHandle",
    };
    // Control types of the browser's own containers (pane, custom, group, tab list); everything else is a leaf for this purpose.
    static readonly HashSet<int> Containers = [UiaIds.Pane, UiaIds.Custom, UiaIds.Group, UiaIds.Tab];

    public UiaReader(Func<Browser, string>? dataFolder = null)
    {
        this.dataFolder = dataFolder ?? BrowserDiscovery.DataFolder;
        var uia = session.Automation;
        cache = uia->CreateCacheRequest();
        foreach (var id in new[] { UiaIds.Name, UiaIds.ControlType, UiaIds.ClassName, UiaIds.RuntimeId, UiaIds.IsSelected, UiaIds.Value })
            cache->AddProperty((UIA_PROPERTY_ID)id);   // elements stay live (the default mode): the walk asks each container for its children
        rootViewIs = session.StringIs(UiaIds.ClassName, RootViewClass);
        documentIs = session.ControlTypeIs(UiaIds.Document);
        everything = session.True();
    }

    /// The window's tabs in strip order and the active tab's URL; null when the window could not be read (the caller treats
    /// that as transient). A window that is not positively a normal one (private, guest, or anything unrecognized) gives
    /// back no tabs and was not read beyond its root view's name.
    public RawWindow? TryRead(BrowserWindow w)
    {
        IUIAutomationElement* root = null, rootView = null;
        try
        {
            root = session.Automation->ElementFromHandle(new HWND(w.Hwnd));
            if (root is null) return null;
            bool normal = false, settled = false;
            for (int attempt = 0; attempt < ClassifyAttempts && !settled; attempt++)
            {
                if (rootView is not null) { rootView->Release(); rootView = null; }
                var before = User32.Title(w.Hwnd);
                rootView = root->FindFirstBuildCache((TreeScope)UiaIds.ScopeChildren, rootViewIs, cache);
                if (rootView is null) return null;   // a tree still being built cannot be classified: never "normal"
                var name = UiaSession.Cached(rootView, UiaIds.Name) as string ?? "";
                settled = User32.Title(w.Hwnd) == before;   // the title moved while we looked: compare again
                normal = settled && PrivateWindowDetector.IsNormal(before, name, () => NamesIn(dataFolder(w.Browser)).Current());
            }
            if (!settled) return null;
            if (!normal) return new RawWindow(w.Hwnd, true, [], null);

            var state = new Walk();
            Descend(rootView, 0, state);
            if (state.Failed) return null;
            return new RawWindow(w.Hwnd, false, state.Tabs, string.IsNullOrWhiteSpace(state.ActiveUrl) ? null : state.ActiveUrl);
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        finally
        {
            if (rootView is not null) rootView->Release();
            if (root is not null) root->Release();
        }
    }

    ProfileNames NamesIn(string folder) => profileNames.TryGetValue(folder, out var n) ? n : profileNames[folder] = new ProfileNames(folder);

    sealed class Walk
    {
        public readonly List<RawTab> Tabs = [];
        public string? ActiveUrl;
        public int Queries;
        public bool Failed;
    }

    /// Depth-first in tree order over the browser's own UI. Tabs are collected; page content is never entered.
    void Descend(IUIAutomationElement* parent, int depth, Walk s)
    {
        if (s.Failed) return;
        if (depth >= MaxDepth || ++s.Queries > MaxQueries) { s.Failed = true; return; }   // not the layout we know: give up rather than guess
        IUIAutomationElementArray* kids = parent->FindAllBuildCache((TreeScope)UiaIds.ScopeChildren, everything, cache);
        if (kids is null) { s.Failed = true; return; }
        try
        {
            int count = kids->Length;
            for (int i = 0; i < count && !s.Failed; i++)
            {
                IUIAutomationElement* e = kids->GetElement(i);
                if (e is null) { s.Failed = true; return; }
                try
                {
                    int type = UiaSession.Cached(e, UiaIds.ControlType) is int t ? t : 0;
                    string cls = UiaSession.Cached(e, UiaIds.ClassName) as string ?? "";
                    if (type == UiaIds.TabItem)
                    {
                        if (cls != TabClass) continue;   // tab-like controls that are not the strip's tabs
                        var title = TabNames.Clean(UiaSession.Cached(e, UiaIds.Name) as string ?? "");
                        s.Tabs.Add(new RawTab(UiaSession.RuntimeIdText(UiaSession.Cached(e, UiaIds.RuntimeId)), title, UiaSession.Cached(e, UiaIds.IsSelected) is true, s.Tabs.Count));
                    }
                    else if (type == UiaIds.Document) s.ActiveUrl ??= UiaSession.Cached(e, UiaIds.Value) as string ?? "";
                    else if (cls == "MultiContentsView" || cls == "WebView") s.ActiveUrl ??= FirstDocumentUrl(e);
                    else if (Containers.Contains(type) && !NotEntered.Contains(cls)) Descend(e, depth + 1, s);
                }
                finally { e->Release(); }
            }
        }
        finally { kids->Release(); }
    }

    /// The first Document under the page host, in tree order: the page's own root, reached before any of its content.
    string? FirstDocumentUrl(IUIAutomationElement* host)
    {
        IUIAutomationElement* doc = host->FindFirstBuildCache((TreeScope)UiaIds.ScopeDescendants, documentIs, cache);
        if (doc is null) return null;
        try { return UiaSession.Cached(doc, UiaIds.Value) as string; }
        finally { doc->Release(); }
    }

    public void Dispose()
    {
        foreach (var c in new[] { rootViewIs, documentIs, everything }) if (c is not null) c->Release();
        rootViewIs = documentIs = everything = null;
        if (cache is not null) { cache->Release(); cache = null; }
        session.Dispose();
    }
}
