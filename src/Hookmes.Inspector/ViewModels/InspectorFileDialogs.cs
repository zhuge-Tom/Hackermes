using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.ViewModels;

public sealed record InspectorFileType(string Name, IReadOnlyList<string> Patterns);
public sealed record InspectorFileDialogRequest(
    string Title,
    string SuggestedPath,
    IReadOnlyList<InspectorFileType> FileTypes)
{
    public string SuggestedFileName => System.IO.Path.GetFileName(SuggestedPath);
}

/// <summary>UI-neutral delegates injected by Views; ViewModels never reference platform storage APIs.</summary>
public sealed record InspectorFileDialogDelegates(
    Func<InspectorFileDialogRequest, CancellationToken, Task<string?>> OpenAsync,
    Func<InspectorFileDialogRequest, CancellationToken, Task<string?>> SaveAsync,
    Func<string, CancellationToken, Task<bool>> ConfirmAsync);
