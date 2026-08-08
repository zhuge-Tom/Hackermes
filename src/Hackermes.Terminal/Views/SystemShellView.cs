using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Hackermes.Platform.Services;
using Hackermes.Terminal.Services;
using Iciclecreek.Terminal;
using System;

namespace Hackermes.Terminal.Views;

/// <summary>A persistent PTY-backed system shell hosted by the Dock overlay.</summary>
public sealed class SystemShellView : UserControl, INonReloadableTabHost, ITabContentReleasable
{
    private readonly TerminalControl _terminal;
    private readonly ShellLaunchSpec _shell;
    private bool _launched;
    private bool _released;

    public SystemShellView(ShellCommandService shellService)
    {
        ArgumentNullException.ThrowIfNull(shellService);
        _shell = shellService.ResolveInteractiveShell();

        _terminal = new TerminalControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            FontFamily = "Cascadia Mono,Consolas,monospace",
            FontSize = 12,
            BufferSize = 5000
        };

        Content = _terminal;
        Loaded += OnLoaded;
    }

    public string ShellName => _shell.DisplayName;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_launched || _released)
            return;

        _launched = true;
        _terminal.LaunchProcess(_shell.WorkingDirectory, _shell.Process, _shell.Arguments);
    }

    public void OnTabBecameVisible() => _terminal.Focus();

    public void OnTabBecameHidden()
    {
        // Deliberately keep the PTY alive while another tab is selected.
    }

    public void ReleaseTabResources()
    {
        if (_released)
            return;

        _released = true;
        Loaded -= OnLoaded;

        try
        {
            _terminal.Kill();
            _terminal.WaitForExit(1000);
        }
        catch (Exception)
        {
            // The tab may be released before the PTY process was launched.
        }

        (_terminal as IDisposable)?.Dispose();
        Content = null;
    }
}
