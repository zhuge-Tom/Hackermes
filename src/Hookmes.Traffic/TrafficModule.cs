using Hookmes.Base;
using Hookmes.Traffic.Rules;
using Hookmes.Traffic.Repeater;
using Hookmes.Traffic.Comparison;
using Hookmes.Traffic.Annotations;
using Hookmes.Traffic.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Hookmes.Traffic;

public sealed class TrafficModule : IModule
{
    public string Name => "Traffic";

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<TrafficHistoryPersistence>();
        services.AddSingleton<ITrafficHistoryPersistence>(sp => sp.GetRequiredService<TrafficHistoryPersistence>());
        services.AddSingleton<TrafficStore>();
        services.AddSingleton<ITrafficStore>(sp => sp.GetRequiredService<TrafficStore>());
        services.AddSingleton<TrafficRuleSet>();
        services.AddSingleton<ITrafficRuleSet>(sp => sp.GetRequiredService<TrafficRuleSet>());
        services.AddSingleton<TrafficRuleManager>();
        services.AddSingleton<ITrafficRuleManager>(sp => sp.GetRequiredService<TrafficRuleManager>());
        services.AddSingleton<TrafficService>();
        services.AddSingleton<ITrafficService>(sp => sp.GetRequiredService<TrafficService>());
        services.AddSingleton<RepeaterService>();
        services.AddSingleton<IRepeaterService>(sp => sp.GetRequiredService<RepeaterService>());
        services.AddSingleton<TrafficComparisonService>();
        services.AddSingleton<ITrafficComparisonService>(sp => sp.GetRequiredService<TrafficComparisonService>());
        services.AddSingleton<TrafficAnnotationService>();
        services.AddSingleton<ITrafficAnnotationService>(sp => sp.GetRequiredService<TrafficAnnotationService>());
    }

    public void Initialize(IServiceProvider serviceProvider)
    {
        // Resolve eagerly so persisted rules are loaded before the first capture starts.
        _ = serviceProvider.GetRequiredService<ITrafficRuleManager>();
    }
}
