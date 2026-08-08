using System;
using System.Runtime.InteropServices;

namespace Hackermes.Cdp.ComInterop;

/// <summary>
/// <c>ICoreWebView2</c> 的 vtable 直调。
/// <para>
/// 槽位序号来自 WebView2 SDK 头文件里接口的方法声明顺序(含 <c>[propget]</c> 属性方法,
/// 它们同样占用槽位 —— 漏数这些是最容易犯的错)。前 3 个槽位属于 IUnknown,故业务方法从 3 起。
/// </para>
/// <para>
/// 校验方式:提取 <c>MIDL_INTERFACE("76eceacb-...")</c> 到接口结束之间所有
/// <c>STDMETHODCALLTYPE</c> 声明并从 3 开始编号。改 SDK 版本后应重新核对。
/// </para>
/// </summary>
internal static unsafe class ICoreWebView2VTable
{
    public static readonly Guid IID = new("76eceacb-0462-4d94-ac83-423a6793775e");

    private const int SlotGetSettings = 3;
    private const int SlotGetSource = 4;
    private const int SlotNavigate = 5;
    private const int SlotAddScriptToExecuteOnDocumentCreated = 27;
    private const int SlotExecuteScript = 29;
    private const int SlotReload = 31;
    private const int SlotCallDevToolsProtocolMethod = 36;
    private const int SlotGetCanGoBack = 38;
    private const int SlotGetCanGoForward = 39;
    private const int SlotGoBack = 40;
    private const int SlotGoForward = 41;
    private const int SlotGetDevToolsProtocolEventReceiver = 42;
    private const int SlotStop = 43;
    private const int SlotGetDocumentTitle = 48;
    private const int SlotOpenDevToolsWindow = 51;

    private static nint Slot(nint obj, int index) => *(nint*)(*(nint*)obj + index * nint.Size);

    public static int GetSource(nint obj, out nint uriPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, SlotGetSource);
        nint result;
        var hr = fn(obj, &result);
        uriPtr = result;
        return hr;
    }

    public static int GetDocumentTitle(nint obj, out nint titlePtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, SlotGetDocumentTitle);
        nint result;
        var hr = fn(obj, &result);
        titlePtr = result;
        return hr;
    }

    public static int Navigate(nint obj, string uri)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(obj, SlotNavigate);
        var uriPtr = ComHelper.AllocString(uri);

        try
        {
            return fn(obj, uriPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(uriPtr);
        }
    }

    public static int Reload(nint obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotReload);
        return fn(obj);
    }

    public static int Stop(nint obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotStop);
        return fn(obj);
    }

    public static int GoBack(nint obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotGoBack);
        return fn(obj);
    }

    public static int GoForward(nint obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotGoForward);
        return fn(obj);
    }

    public static int GetCanGoBack(nint obj, out bool canGoBack)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(obj, SlotGetCanGoBack);
        int value;
        var hr = fn(obj, &value);
        canGoBack = value != 0;
        return hr;
    }

    public static int GetCanGoForward(nint obj, out bool canGoForward)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(obj, SlotGetCanGoForward);
        int value;
        var hr = fn(obj, &value);
        canGoForward = value != 0;
        return hr;
    }

    public static int OpenDevToolsWindow(nint obj)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotOpenDevToolsWindow);
        return fn(obj);
    }

    /// <param name="handler">ICoreWebView2CallDevToolsProtocolMethodCompletedHandler 的 CCW 指针</param>
    public static int CallDevToolsProtocolMethod(nint obj, string method, string parametersJson, nint handler)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Slot(obj, SlotCallDevToolsProtocolMethod);
        var methodPtr = ComHelper.AllocString(method);
        var paramsPtr = ComHelper.AllocString(parametersJson);

        try
        {
            return fn(obj, methodPtr, paramsPtr, handler);
        }
        finally
        {
            Marshal.FreeCoTaskMem(methodPtr);
            Marshal.FreeCoTaskMem(paramsPtr);
        }
    }

    /// <summary>
    /// 取某个 CDP 事件的接收器。<strong>接收器是按事件名逐个创建的</strong>,不是一条总线,
    /// 因此上层需要维护"事件名 → 接收器"的表并做引用计数。
    /// </summary>
    public static int GetDevToolsProtocolEventReceiver(nint obj, string eventName, out nint receiver)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(obj, SlotGetDevToolsProtocolEventReceiver);
        var namePtr = ComHelper.AllocString(eventName);

        try
        {
            nint result;
            var hr = fn(obj, namePtr, &result);
            receiver = result;
            return hr;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    /// <summary>
    /// 文档级脚本预注入:在页面自身任何脚本之前执行,是 Page Agent 的落地方式之一。
    /// <para>
    /// 已知平台缺陷:与 <c>NavigateWithWebResourceRequest</c> 同用时不生效。
    /// Hackermes 主路径走 CDP 的 <c>Page.addScriptToEvaluateOnNewDocument</c>,
    /// 因为后者额外支持 <c>worldName</c>(隔离世界),这里保留作为备用。
    /// </para>
    /// </summary>
    public static int AddScriptToExecuteOnDocumentCreated(nint obj, string script, nint handler)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(obj, SlotAddScriptToExecuteOnDocumentCreated);
        var scriptPtr = ComHelper.AllocString(script);

        try
        {
            return fn(obj, scriptPtr, handler);
        }
        finally
        {
            Marshal.FreeCoTaskMem(scriptPtr);
        }
    }

    public static int ExecuteScript(nint obj, string script, nint handler)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(obj, SlotExecuteScript);
        var scriptPtr = ComHelper.AllocString(script);

        try
        {
            return fn(obj, scriptPtr, handler);
        }
        finally
        {
            Marshal.FreeCoTaskMem(scriptPtr);
        }
    }
}

/// <summary>
/// <c>ICoreWebView2DevToolsProtocolEventReceiver</c> — 单个 CDP 事件的订阅入口。
/// WebView2.h:MIDL_INTERFACE("b32ca51a-8371-45e9-9317-af021d080367")
/// </summary>
internal static unsafe class ICoreWebView2DevToolsProtocolEventReceiverVTable
{
    private const int SlotAdd = 3;
    private const int SlotRemove = 4;

    private static nint Slot(nint obj, int index) => *(nint*)(*(nint*)obj + index * nint.Size);

    public static int Add(nint obj, nint handler, out long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(obj, SlotAdd);
        long result;
        var hr = fn(obj, handler, &result);
        token = result;
        return hr;
    }

    public static int Remove(nint obj, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, long, int>)Slot(obj, SlotRemove);
        return fn(obj, token);
    }
}

/// <summary>
/// <c>ICoreWebView2DevToolsProtocolEventReceivedEventArgs</c>
/// WebView2.h:MIDL_INTERFACE("653c2959-bb3a-4377-8632-b58ada4e66c4")
/// </summary>
internal static unsafe class ICoreWebView2DevToolsProtocolEventReceivedEventArgsVTable
{
    private const int SlotGetParameterObjectAsJson = 3;

    private static nint Slot(nint obj, int index) => *(nint*)(*(nint*)obj + index * nint.Size);

    public static int GetParameterObjectAsJson(nint obj, out nint jsonPtr)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, SlotGetParameterObjectAsJson);
        nint result;
        var hr = fn(obj, &result);
        jsonPtr = result;
        return hr;
    }
}
