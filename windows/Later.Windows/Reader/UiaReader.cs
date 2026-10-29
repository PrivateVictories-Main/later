using Later.Windows.Native;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Later.Windows.Reader;

/// One UI Automation pass over a browser window. Reads only (properties cached in one call); never registers an event
/// handler, never invokes a pattern, never touches the window. MTA thread.
internal sealed unsafe class UiaReader : IDisposable
{
    readonly UiaSession session = new();
    IUIAutomationCacheRequest* cache;
    IUIAutomationCondition* tabsAndDocuments;

    const string TabClass = "Tab", RootViewClass = "BrowserRootView", ProfileButtonClass = "AvatarToolbarButton";

    public UiaReader()
    {
        var uia = session.Automation;
        cache = uia->CreateCacheRequest();
        foreach (var id in new[] { UiaIds.Name, UiaIds.ControlType, UiaIds.ClassName, UiaIds.RuntimeId, UiaIds.IsSelected, UiaIds.Value })
            cache->AddProperty((UIA_PROPERTY_ID)id);
        cache->AutomationElementMode = (AutomationElementMode)0;   // None: cached values only
        var tab = session.ControlTypeIs(UiaIds.TabItem); var doc = session.ControlTypeIs(UiaIds.Document);
        var root = session.StringIs(UiaIds.ClassName, RootViewClass); var profile = session.StringIs(UiaIds.ClassName, ProfileButtonClass);
        var a = session.Or(tab, doc); var b = session.Or(root, profile);
        tabsAndDocuments = session.Or(a, b);
        foreach (var c in new[] { tab, doc, root, profile, a, b }) c->Release();
    }

    /// The window's tabs in strip order and the active tab's URL; null when the window could not be read (the caller treats that as transient).
    /// The window's chrome is read first and a private window gives back no tabs at all; a window whose root view is not
    /// there (a tree still being built) cannot be classified and also gives null.
    public RawWindow? TryRead(BrowserWindow w)
    {
        IUIAutomationElement* root = null;
        IUIAutomationElementArray* found = null;
        try
        {
            root = session.Automation->ElementFromHandle(new HWND(w.Hwnd));
            if (root is null) return null;
            found = root->FindAllBuildCache((TreeScope)UiaIds.ScopeDescendants, tabsAndDocuments, cache);
            if (found is null) return null;
            int count = found->Length;
            string? rootViewName = null, profileButtonName = null, activeUrl = null;
            var tabs = new List<RawTab>();
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement* e = found->GetElement(i);
                if (e is null) return null;
                try
                {
                    int type = UiaSession.Cached(e, UiaIds.ControlType) is int t ? t : 0;
                    string cls = UiaSession.Cached(e, UiaIds.ClassName) as string ?? "";
                    if (cls == RootViewClass) rootViewName ??= UiaSession.Cached(e, UiaIds.Name) as string ?? "";
                    else if (cls == ProfileButtonClass) profileButtonName ??= UiaSession.Cached(e, UiaIds.Name) as string ?? "";
                    else if (type == UiaIds.Document) { if (activeUrl is null) activeUrl = UiaSession.Cached(e, UiaIds.Value) as string ?? ""; }   // the page's own document comes first
                    else if (type == UiaIds.TabItem && cls == TabClass)   // the tab strip's tabs, not tab-like controls inside a page
                    {
                        var title = TabNames.Clean(UiaSession.Cached(e, UiaIds.Name) as string ?? "");
                        tabs.Add(new RawTab(UiaSession.RuntimeIdText(UiaSession.Cached(e, UiaIds.RuntimeId)), title, UiaSession.Cached(e, UiaIds.IsSelected) is true, tabs.Count));
                    }
                }
                finally { e->Release(); }
            }
            if (rootViewName is null) return null;   // cannot tell private from normal: never "normal"
            if (PrivateWindowDetector.IsPrivate(new RawWindowInfo(w.Browser, rootViewName, profileButtonName))) return new RawWindow(w.Hwnd, true, [], null);
            return new RawWindow(w.Hwnd, false, tabs, string.IsNullOrWhiteSpace(activeUrl) ? null : activeUrl);
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        finally
        {
            if (found is not null) found->Release();
            if (root is not null) root->Release();
        }
    }

    public void Dispose()
    {
        if (cache is not null) { cache->Release(); cache = null; }
        if (tabsAndDocuments is not null) { tabsAndDocuments->Release(); tabsAndDocuments = null; }
        session.Dispose();
    }
}
