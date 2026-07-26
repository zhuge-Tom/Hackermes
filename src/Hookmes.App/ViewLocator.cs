using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Hookmes.Base.Mvvm;
using System;
using System.Collections.Generic;

namespace Hookmes.App;

/// <summary>
/// ViewModel → View 解析。
/// <para>
/// 用显式字典而非"把 ViewModel 换成 View 再反射 Activator.CreateInstance"的约定式做法:
/// 反射方案在裁剪与 AOT 下会静默失败,而这里的错误在编译期就能发现。
/// </para>
/// <para>
/// 大多数 View 其实是在 Tab 工厂里直接 new 出来的,这里只覆盖真正走内容绑定的场景。
/// 各模块通过 <see cref="Register{TViewModel}"/> 自注册,宿主不需要认识它们的类型。
/// </para>
/// </summary>
public class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Map = new();

    public static void Register<TViewModel>(Func<Control> factory) where TViewModel : ViewModelBase =>
        Map[typeof(TViewModel)] = factory;

    public Control Build(object? data)
    {
        if (data is null)
            return new TextBlock { Text = string.Empty };

        if (Map.TryGetValue(data.GetType(), out var factory))
            return factory();

        return new TextBlock
        {
            Text = $"未注册视图: {data.GetType().Name}",
            Margin = new Avalonia.Thickness(12)
        };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
