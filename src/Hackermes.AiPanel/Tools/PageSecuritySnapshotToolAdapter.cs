using Hackermes.Platform.Services;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Tools;

public sealed class PageSecuritySnapshotToolAdapter(IPageSecuritySnapshotService snapshots)
{
    public void RegisterAll(IAiToolRegistry registry)
    {
        registry.Register(new AiToolDefinition(
            "page_security_snapshot",
            "Read a bounded, value-free security snapshot for the exact current embedded-browser page. Returns URL/origin/title, form and script metadata, selected security-header/CSP flags, and aggregate cookie flags; never returns cookie, token, form-field, storage, body, or inline-script values.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            }),
            AiToolRisk.ReadOnly,
            ReadAsync));
    }

    private async ValueTask<ToolResult> ReadAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocation.PageId))
            return ToolResult.Fail("No active browser page.");
        try
        {
            var snapshot = await snapshots.ReadAsync(invocation.PageId, cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok(JsonSerializer.Serialize(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ToolResult.Fail("The current page security snapshot is unavailable.");
        }
    }
}
