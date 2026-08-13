using Hackermes.Platform.Services;
using System.Text.Json;
using System.Threading.Tasks;

namespace Hackermes.AiPanel.Tools;

public sealed class PageContextToolAdapter(IPageContextQueryService pages)
{
    public void RegisterAll(IAiToolRegistry registry)
    {
        registry.Register(new AiToolDefinition(
            "page_context",
            "Read the current embedded-browser tab's page ID, URL, title, CDP status, and Page Agent status.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            }),
            AiToolRisk.ReadOnly,
            (invocation, _) => ValueTask.FromResult(Read(invocation))));
    }

    private ToolResult Read(ToolInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.PageId))
            return ToolResult.Fail("No active browser page.");

        var page = pages.Read(invocation.PageId);
        return page is null
            ? ToolResult.Fail($"No open browser page matches '{invocation.PageId}'.")
            : ToolResult.Ok(JsonSerializer.Serialize(page));
    }
}
