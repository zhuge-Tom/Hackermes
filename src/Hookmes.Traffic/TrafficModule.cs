using Hookmes.Base;
using Hookmes.Traffic.Rules;
using Hookmes.Traffic.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hookmes.Traffic;

public sealed class TrafficModule : IModule
{
    public string Name => "Traffic";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<TrafficStore>();
        services.AddSingleton<ITrafficStore>(sp => sp.GetRequiredService<TrafficStore>());
        services.AddSingleton<TrafficRuleSet>();
        services.AddSingleton<ITrafficRuleSet>(sp => sp.GetRequiredService<TrafficRuleSet>());
        services.AddSingleton<TrafficService>();
        services.AddSingleton<ITrafficService>(sp => sp.GetRequiredService<TrafficService>());
    }

    public void Initialize(IServiceProvider serviceProvider) { }
}
