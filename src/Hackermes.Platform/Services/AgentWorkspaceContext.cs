using System;
using System.Collections.Generic;

namespace Hackermes.Platform.Services;

public sealed record AgentWorkspaceInfo(string Id, string Name);

public interface IAgentWorkspaceContext
{
    string CurrentId { get; }
    void SetCurrent(string workspaceId);
}

public interface IAgentWorkspaceDirectory
{
    IReadOnlyList<AgentWorkspaceInfo> List();
    AgentWorkspaceInfo Create(string name);
}

public sealed class AgentWorkspaceContext : IAgentWorkspaceContext
{
    public string CurrentId { get; private set; } = string.Empty;

    public void SetCurrent(string workspaceId) =>
        CurrentId = string.IsNullOrWhiteSpace(workspaceId) ? string.Empty : workspaceId.Trim();
}
