using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Accessibility;

namespace Later.Windows.Native;

internal static partial class Com
{
    [LibraryImport("ole32.dll")] internal static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("oleaut32.dll")] internal static partial int VariantClear(nint variant);
}

/// UI Automation properties the reader asks for (ids from UIAutomationClient.h).
internal static class UiaIds
{
    public const int RuntimeId = 30000, ControlType = 30003, Name = 30005, AutomationId = 30011, ClassName = 30012, Value = 30045, IsSelected = 30079;
    public const int TabItem = 50019, Document = 50030;
    public const int ScopeChildren = 2, ScopeDescendants = 4;
}

/// One IUIAutomation on the calling (MTA) thread. Raw COM pointers: the caller releases what it gets.
internal sealed unsafe class UiaSession : IDisposable
{
    public IUIAutomation* Automation { get; private set; }
    readonly bool uninitialize;

    public UiaSession()
    {
        int hr = Com.CoInitializeEx(0, 0 /*COINIT_MULTITHREADED*/);
        uninitialize = hr >= 0;   // S_OK and S_FALSE both need a CoUninitialize; RPC_E_CHANGED_MODE (an STA thread) does not
        var clsid = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");   // CUIAutomation
        var iid = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");     // IUIAutomation
        hr = Com.CoCreateInstance(in clsid, 0, 1 /*CLSCTX_INPROC_SERVER*/, in iid, out var instance);
        if (hr < 0 || instance == 0)
        {
            if (uninitialize) Com.CoUninitialize();
            throw new Win32Exception(hr, "UI Automation is not available");
        }
        Automation = (IUIAutomation*)instance;
    }

    public void Dispose()
    {
        if (Automation is not null) { Automation->Release(); Automation = null; if (uninitialize) Com.CoUninitialize(); }
    }

    /// A condition `ControlType == type`; the caller releases it.
    public IUIAutomationCondition* ControlTypeIs(int type)
    {
        VARIANT v = default;
        Marshal.GetNativeVariantForObject(type, (nint)(&v));
        IUIAutomationCondition* condition;
        Check(Automation->CreatePropertyCondition((UIA_PROPERTY_ID)UiaIds.ControlType, v, &condition));
        return condition;
    }

    public IUIAutomationCondition* Or(IUIAutomationCondition* a, IUIAutomationCondition* b)
    {
        IUIAutomationCondition* condition;
        Check(Automation->CreateOrCondition(a, b, &condition));
        return condition;
    }

    public IUIAutomationCondition* True()
    {
        IUIAutomationCondition* condition;
        Check(Automation->CreateTrueCondition(&condition));
        return condition;
    }

    /// A property as the cache request captured it: string, bool, int or int[] (RuntimeId); null when empty or unsupported.
    public static object? Cached(IUIAutomationElement* element, int propertyId)
    {
        VARIANT v = default;
        if (element->GetCachedPropertyValue((UIA_PROPERTY_ID)propertyId, &v).Failed) return null;
        return Take(&v);
    }

    /// A property read live from the element.
    public static object? Current(IUIAutomationElement* element, int propertyId)
    {
        VARIANT v = default;
        if (element->GetCurrentPropertyValue((UIA_PROPERTY_ID)propertyId, &v).Failed) return null;
        return Take(&v);
    }

    static object? Take(VARIANT* v)
    {
        try { return Marshal.GetObjectForNativeVariant((nint)v) is { } o && o is string or bool or int or int[] ? o : null; }
        finally { _ = Com.VariantClear((nint)v); }
    }

    public static string RuntimeIdText(object? id) => id is int[] ids ? string.Join('.', ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) : "";

    public static void Check(HRESULT hr)
    {
        if (hr.Failed) throw new COMException("UI Automation call failed", hr.Value);
    }
}
