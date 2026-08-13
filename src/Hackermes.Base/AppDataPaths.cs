using System;
using System.IO;

namespace Hackermes.Base;

/// <summary>
/// Resolves Hackermes-owned persistent data. Tests and release acceptance may set an
/// explicit absolute root so Windows Known Folder resolution cannot leak state into the
/// operator's real profile.
/// </summary>
public static class AppDataPaths
{
    public const string RootEnvironmentVariable = "HACKERMES_DATA_ROOT";

    public static bool HasExplicitRoot =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RootEnvironmentVariable));

    public static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathFullyQualified(configured))
                    throw new InvalidOperationException($"{RootEnvironmentVariable} must be an absolute path.");
                var resolved = Path.GetFullPath(configured);
                if (string.Equals(resolved, Path.GetPathRoot(resolved), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{RootEnvironmentVariable} cannot be a drive or filesystem root.");
                return resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.GetFullPath(Path.Combine(local, "Hackermes"));
        }
    }

    public static string Resolve(params string[] relativeParts)
    {
        var root = Root;
        var candidate = root;
        foreach (var part in relativeParts)
        {
            if (string.IsNullOrWhiteSpace(part) || Path.IsPathFullyQualified(part))
                throw new ArgumentException("App data path parts must be non-empty relative paths.", nameof(relativeParts));
            candidate = Path.Combine(candidate, part);
        }

        candidate = Path.GetFullPath(candidate);
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"App data path escaped {RootEnvironmentVariable}: {candidate}");
        return candidate;
    }
}
