namespace Hookmes.Inspector.ViewModels;

/// <summary>Persists non-sensitive recent file locations without coupling Inspector to platform settings.</summary>
public interface IRecentTrafficPathService
{
    string? LastArchivePath { get; }
    string? LastRulesPath { get; }
    string NormalizePath(string path);
    void RememberArchivePath(string path);
    void RememberRulesPath(string path);
}
