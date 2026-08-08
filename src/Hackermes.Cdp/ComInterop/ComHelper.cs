using System;
using System.Runtime.InteropServices;

namespace Hackermes.Cdp.ComInterop;

/// <summary>
/// 裸 COM 指针操作。
/// <para>
/// 这里刻意不依赖 <c>Microsoft.Web.WebView2.Core</c> 的托管封装 ——
/// Avalonia 的 WebView 只给出 <c>ICoreWebView2*</c> 裸指针,而托管封装没有从指针构造的公开途径。
/// 走 vtable 直调既能拿到全部能力,也不引入运行时反射,对裁剪与 AOT 友好。
/// </para>
/// </summary>
internal static unsafe class ComHelper
{
    public static int QueryInterface(nint comObj, in Guid iid, out nint ppv)
    {
        var vtable = *(nint*)comObj;
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)*(nint*)vtable;

        fixed (Guid* pIid = &iid)
        {
            nint result;
            var hr = fn(comObj, pIid, &result);
            ppv = result;
            return hr;
        }
    }

    public static uint AddRef(nint comObj)
    {
        var vtable = *(nint*)comObj;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)*(nint*)(vtable + nint.Size);
        return fn(comObj);
    }

    public static uint Release(nint comObj)
    {
        if (comObj == 0)
            return 0;

        var vtable = *(nint*)comObj;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)*(nint*)(vtable + 2 * nint.Size);
        return fn(comObj);
    }

    /// <summary>读取由被调用方用 CoTaskMemAlloc 分配的字符串,并释放它。</summary>
    public static string? ReadAndFreeString(nint ptr)
    {
        if (ptr == 0)
            return null;

        var s = Marshal.PtrToStringUni(ptr);
        Marshal.FreeCoTaskMem(ptr);
        return s;
    }

    /// <summary>读取由调用方持有的字符串(事件参数等),不释放。</summary>
    public static string? ReadString(nint ptr) =>
        ptr == 0 ? null : Marshal.PtrToStringUni(ptr);

    public static nint AllocString(string? s) =>
        s is null ? 0 : Marshal.StringToCoTaskMemUni(s);

    /// <summary>把 HRESULT 转成可读异常。</summary>
    public static Exception ToException(int hr, string context) =>
        Marshal.GetExceptionForHR(hr) is { } inner
            ? new InvalidOperationException($"{context} 失败 (HRESULT 0x{hr:X8})", inner)
            : new InvalidOperationException($"{context} 失败 (HRESULT 0x{hr:X8})");

    /// <summary>作用域内持有 COM 指针,离开时 Release。</summary>
    public readonly struct Scope(nint ptr) : IDisposable
    {
        public nint Ptr { get; } = ptr;

        public void Dispose() => Release(Ptr);
    }
}
