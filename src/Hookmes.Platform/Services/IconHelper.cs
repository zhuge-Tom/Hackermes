using Avalonia;
using Avalonia.Media;

namespace Hookmes.Platform.Services;

/// <summary>
/// 从主题资源里按 Key 取矢量图标。注册处只写字符串 Key,渲染处绑 <see cref="StreamGeometry"/>,
/// 这样模块无需引用任何图标库。
/// <para>
/// <see cref="GetIcon(string[])"/> 支持依次尝试多个候选 Key ——
/// 主题库(Semi)在大版本间改过图标名,多给几个备选可以避免升级即全图标丢失。
/// </para>
/// </summary>
public static class IconHelper
{
    public static StreamGeometry? GetIcon(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        var app = Application.Current;
        if (app is null)
            return null;

        return app.TryGetResource(key, app.ActualThemeVariant, out var resource)
               && resource is StreamGeometry geometry
            ? geometry
            : null;
    }

    /// <summary>按顺序尝试候选 Key,返回第一个命中的图标。</summary>
    public static StreamGeometry? GetIcon(params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            var icon = GetIcon(key);
            if (icon is not null)
                return icon;
        }

        return null;
    }
}
