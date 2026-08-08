using Hackermes.Base;
using Hackermes.Dock.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hackermes.Dock;

/// <summary>
/// 布局模块。它只提供承载能力,自己不注册任何 Tab ——
/// Tab 全部由功能模块在各自的 <c>Initialize</c> 里向 <c>IDockLayoutRegistry</c> 登记。
/// <para>装配顺序上必须排在所有功能模块之前。</para>
/// </summary>
public sealed class DockModule : IModule
{
    public string Name => "Dock";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<DockLayoutViewModel>();
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
    }
}
