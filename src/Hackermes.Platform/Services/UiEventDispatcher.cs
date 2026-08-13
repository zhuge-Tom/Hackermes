using Avalonia.Threading;
using System;

namespace Hackermes.Platform.Services;

/// <summary>Explicit seam for delivering view-facing events on the UI thread.</summary>
public interface IUiEventDispatcher
{
    void Post(Action action);
}

public sealed class AvaloniaUiEventDispatcher : IUiEventDispatcher
{
    public void Post(Action action) => UiThreadBridge.Post(action, DispatcherPriority.Background);
}
