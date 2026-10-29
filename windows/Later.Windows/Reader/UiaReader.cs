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

    public UiaReader()
    {
        var uia = session.Automation;
        cache = uia->CreateCacheRequest();
        foreach (var id in new[] { UiaIds.Name, UiaIds.ControlType, UiaIds.RuntimeId, UiaIds.IsSelected, UiaIds.Value })
            cache->AddProperty((UIA_PROPERTY_ID)id);
        cache->AutomationElementMode = (AutomationElementMode)0;   // None: cached values only
        var tab = session.ControlTypeIs(UiaIds.TabItem); var doc = session.ControlTypeIs(UiaIds.Document);
        tabsAndDocuments = session.Or(tab, doc);
        tab->Release(); doc->Release();
    }

    /// The window's tabs in strip order and the active tab's URL; null when the window could not be read (the caller treats that as transient).
    /// A private window gives back no tabs at all.
    public RawWindow? TryRead(BrowserWindow w)
    {
        IUIAutomationElement* root = null;
        IUIAutomationElementArray* found = null;
        try
        {
            root = session.Automation->ElementFromHandle(new HWND(w.Hwnd));
            if (root is null) return null;
            string uiaName = UiaSession.Current(root, UiaIds.Name) as string ?? "";
            if (PrivateWindowDetector.IsPrivate(new RawWindowInfo(w.Browser, w.Title, uiaName))) return new RawWindow(w.Hwnd, true, [], null);

            found = root->FindAllBuildCache((TreeScope)UiaIds.ScopeDescendants, tabsAndDocuments, cache);
            if (found is null) return null;
            int count = found->Length;
            var tabs = new List<RawTab>(); string? activeUrl = null; bool pastDocument = false;
            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement* e = found->GetElement(i);
                if (e is null) return null;
                try
                {
                    int type = UiaSession.Cached(e, UiaIds.ControlType) is int t ? t : 0;
                    if (type == UiaIds.Document)
                    {
                        if (!pastDocument) { pastDocument = true; activeUrl = UiaSession.Cached(e, UiaIds.Value) as string; }   // the page's own document comes first
                    }
                    else if (type == UiaIds.TabItem && !pastDocument)   // tab-like controls inside the page come after its document
                    {
                        var title = TabNames.Clean(UiaSession.Cached(e, UiaIds.Name) as string ?? "");
                        tabs.Add(new RawTab(UiaSession.RuntimeIdText(UiaSession.Cached(e, UiaIds.RuntimeId)), title, UiaSession.Cached(e, UiaIds.IsSelected) is true, tabs.Count));
                    }
                }
                finally { e->Release(); }
            }
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
