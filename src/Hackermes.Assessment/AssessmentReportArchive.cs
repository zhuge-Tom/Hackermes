using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Hackermes.Base;

namespace Hackermes.Assessment;

/// <summary>
/// Writes one human-browsable folder per assessment job: the redacted Markdown report
/// (findings with PoC), the structured case snapshot, the raw redacted evidence, the audit
/// timeline and (when a signer is available) the signed report document.
/// </summary>
public interface IAssessmentReportArchive
{
    /// <summary>Writes the archive and returns the folder path.</summary>
    string Archive(string jobId);

    /// <summary>Writes the archive and returns its folder path plus the primary report file.</summary>
    IReadOnlyList<string> ArchiveFiles(string jobId);
}

public sealed class AssessmentReportArchive(IAssessmentControlPlane plane, IAssessmentReportExportService? exporter = null)
    : IAssessmentReportArchive
{
    public const int MaximumEvidenceFiles = 200;
    public const int MaximumEvidenceCharacters = 262_144;
    public const int MaximumCaseBytes = 4 * 1024 * 1024;

    public string Archive(string jobId) => ArchiveInternal(jobId).Folder;

    public IReadOnlyList<string> ArchiveFiles(string jobId) => ArchiveInternal(jobId).Files;

    private (string Folder, IReadOnlyList<string> Files) ArchiveInternal(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Assessment job id is required.", nameof(jobId));
        var snapshot = plane.ReadCase(jobId.Trim());
        if (snapshot.Job.Id.Length == 0) throw new ArgumentException($"Job '{jobId}' was not found.");

        var folder = Path.Combine(AppDataPaths.Resolve("reports"), jobId.Trim());
        Directory.CreateDirectory(folder);

        var files = new List<string>();
        var report = plane.ExportReport(jobId, "markdown");
        files.Add(WriteText(folder, "report.md", report));

        var caseJson = JsonSerializer.Serialize(snapshot, DisplayJson);
        files.Add(WriteText(folder, "case.json", caseJson));

        var evidenceDir = Path.Combine(folder, "evidence");
        Directory.CreateDirectory(evidenceDir);
        var evidenceIndex = new StringBuilder();
        for (var i = 0; i < snapshot.Evidence.Count && i < MaximumEvidenceFiles; i++)
        {
            var item = snapshot.Evidence[i];
            var name = $"{i + 1:D2}_{SafeName(item.Source)}.txt";
            var content = Bound(item.Content, MaximumEvidenceCharacters);
            files.Add(WriteText(evidenceDir, name, content));
            evidenceIndex.AppendLine($"- `{item.Id}` — {item.Source}; SHA-256 `{item.Sha256}` → `evidence/{name}`; redacted={item.Redacted}");
        }
        if (snapshot.Evidence.Count > MaximumEvidenceFiles)
            evidenceIndex.AppendLine($"- … {snapshot.Evidence.Count - MaximumEvidenceFiles} more evidence item(s) omitted from this archive.");
        files.Add(WriteText(folder, "evidence/index.md", $"# Evidence index\n\n{evidenceIndex}\n"));

        var auditJson = JsonSerializer.Serialize(snapshot.Audit.OrderBy(value => value.Timestamp), DisplayJson);
        files.Add(WriteText(folder, "audit.json", auditJson));

        if (exporter is not null)
        {
            try
            {
                files.Add(WriteText(folder, "signed-report.json", exporter.Export(jobId)));
            }
            catch
            {
                // Signed export is best-effort; a missing signing key must not block the human archive.
            }
        }

        return (folder, files);
    }

    private static string WriteText(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string SafeName(string source)
    {
        var result = new string(source.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(result) ? "artifact" : result;
    }

    private static readonly JsonSerializerOptions DisplayJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
