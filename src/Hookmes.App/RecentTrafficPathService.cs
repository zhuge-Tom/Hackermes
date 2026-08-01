using Hookmes.Inspector.ViewModels;
using Hookmes.Platform.Services;
using System;
using System.IO;

namespace Hookmes.App;

internal sealed class RecentTrafficPathService(ISettingsService settings) : IRecentTrafficPathService
{
    public string? LastArchivePath => settings.Load().Traffic.LastArchivePath;
    public string? LastRulesPath => settings.Load().Traffic.LastRulesPath;

    public string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is required.", nameof(path));
        return Path.GetFullPath(path.Trim());
    }

    public void RememberArchivePath(string path)
    {
        var normalized = NormalizePath(path);
        settings.Update(value => value.Traffic.LastArchivePath = normalized);
    }

    public void RememberRulesPath(string path)
    {
        var normalized = NormalizePath(path);
        settings.Update(value => value.Traffic.LastRulesPath = normalized);
    }
}
