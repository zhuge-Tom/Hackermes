using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Hackermes.Cdp.ComInterop;

// ─────────────────────────────────────────────────────────────────────────────
// WebView2 回调接口。
//
// 用 .NET 8 起的源生成 COM 互操作([GeneratedComInterface] / [GeneratedComClass]),
// 编译期生成 CCW 与 vtable,不走运行时 IDispatch,AOT 与裁剪安全。
//
// GUID 全部取自 WebView2 SDK 头文件
// (microsoft.web.webview2/1.0.4022.49/build/native/include/WebView2.h),
// 不要凭记忆填写 —— 一位写错就是 QueryInterface 静默失败,回调永远收不到。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>WebView2.h:MIDL_INTERFACE("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")</summary>
[GeneratedComInterface]
[Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint returnObjectAsJson);
}

/// <summary>WebView2.h:MIDL_INTERFACE("e2fda4be-5456-406c-a261-3d452138362c")</summary>
[GeneratedComInterface]
[Guid("e2fda4be-5456-406c-a261-3d452138362c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2DevToolsProtocolEventReceivedEventHandler
{
    [PreserveSig]
    int Invoke(nint sender, nint args);
}

/// <summary>WebView2.h:MIDL_INTERFACE("b99369f3-9b11-47b5-bc6f-8e7895fcea17")</summary>
[GeneratedComInterface]
[Guid("b99369f3-9b11-47b5-bc6f-8e7895fcea17")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint id);
}

/// <summary>WebView2.h:MIDL_INTERFACE("49511172-cc67-4bca-9923-137112f4c4cc")</summary>
[GeneratedComInterface]
[Guid("49511172-cc67-4bca-9923-137112f4c4cc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface ICoreWebView2ExecuteScriptCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, nint resultObjectAsJson);
}

// ── 实现类 ───────────────────────────────────────────────────────────────────
// 回调里绝不能让异常逃逸到原生栈:那会直接终止进程,且没有可用的调用栈。
// 一律吞掉并返回 S_OK。

[GeneratedComClass]
internal sealed partial class CdpMethodCompletedHandler(Action<int, nint> callback)
    : ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    public int Invoke(int errorCode, nint returnObjectAsJson)
    {
        try
        {
            callback(errorCode, returnObjectAsJson);
        }
        catch
        {
            // 有意吞掉,见上方说明。
        }

        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class CdpEventHandler(Action<nint> callback)
    : ICoreWebView2DevToolsProtocolEventReceivedEventHandler
{
    public int Invoke(nint sender, nint args)
    {
        try
        {
            callback(args);
        }
        catch
        {
        }

        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class AddScriptCompletedHandler(Action<int, string?> callback)
    : ICoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler
{
    public int Invoke(int errorCode, nint id)
    {
        try
        {
            callback(errorCode, ComHelper.ReadString(id));
        }
        catch
        {
        }

        return 0;
    }
}

[GeneratedComClass]
internal sealed partial class ExecuteScriptCompletedHandler(Action<int, string?> callback)
    : ICoreWebView2ExecuteScriptCompletedHandler
{
    public int Invoke(int errorCode, nint resultObjectAsJson)
    {
        try
        {
            callback(errorCode, ComHelper.ReadString(resultObjectAsJson));
        }
        catch
        {
        }

        return 0;
    }
}
