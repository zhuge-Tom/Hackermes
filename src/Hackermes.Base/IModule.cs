using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hackermes.Base;

/// <summary>
/// 功能模块契约。装配严格分两趟:先对所有模块调用 <see cref="RegisterServices"/>,
/// 构建容器之后再对所有模块调用 <see cref="Initialize"/>。
/// <para>
/// 因此 <see cref="RegisterServices"/> 期间<strong>不得</strong>解析任何服务;
/// 向注册表登记 Tab / 菜单 / 设置页 / AI 工具一律放在 <see cref="Initialize"/>。
/// </para>
/// </summary>
public interface IModule
{
    /// <summary>模块名,用于诊断日志与启动耗时打点。</summary>
    string Name { get; }

    void RegisterServices(IServiceCollection services);

    void Initialize(IServiceProvider serviceProvider);
}
