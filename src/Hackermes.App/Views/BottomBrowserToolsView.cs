using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hackermes.Base.Events;
using Hackermes.Platform.Events;
using Hackermes.Platform.Services;
using System;

namespace Hackermes.App.Views;

/// <summary>
/// Browser-only actions hosted at the end of the lower DevTools tab strip.
/// Keeping these controls here mirrors browser DevTools: they operate on the
/// inspected page, rather than on page navigation itself.
/// </summary>
internal sealed class BottomBrowserToolsView : StackPanel, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly Button _pickerButton;
    private readonly Button _deviceButton;
    private readonly IDisposable _activeTabSubscription;
    private readonly IDisposable _pickerStateSubscription;
    private readonly IDisposable _deviceStateSubscription;

    private string? _pageId;
    private bool _pickerActive;
    private bool _deviceActive;
    private bool _disposed;

    public BottomBrowserToolsView(IEventBus eventBus)
    {
        _eventBus = eventBus;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 2;
        Margin = new Thickness(6, 0, 8, 0);

        Children.Add(new Border
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(0, 0, 5, 0),
            Background = new SolidColorBrush(Color.Parse("#D9DDE3"))
        });

        _pickerButton = CreateButton("\u2316", "Pick an element in the page (click again to cancel).");
        _deviceButton = CreateButton("\u25A3", "Toggle responsive mobile viewport for this page.");
        _pickerButton.Click += (_, _) => TogglePicker();
        _deviceButton.Click += (_, _) => ToggleDeviceMode();
        Children.Add(_pickerButton);
        Children.Add(_deviceButton);

        _activeTabSubscription = eventBus.SubscribeDisposable<ActiveContentTabChangedEvent>(OnActiveTabChanged);
        _pickerStateSubscription = eventBus.SubscribeDisposable<ElementPickerStateChangedEvent>(OnPickerStateChanged);
        _deviceStateSubscription = eventBus.SubscribeDisposable<BrowserDeviceModeStateChangedEvent>(OnDeviceStateChanged);
        UpdateButtons();
    }

    private static Button CreateButton(string icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 16,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    private void OnActiveTabChanged(ActiveContentTabChangedEvent change) => UiThreadBridge.Post(() =>
    {
        _pageId = change.TabId is { } id && id.StartsWith("page-", StringComparison.Ordinal) ? id : null;
        _pickerActive = false;
        _deviceActive = false;
        UpdateButtons();
    });

    private void OnPickerStateChanged(ElementPickerStateChangedEvent state) => UiThreadBridge.Post(() =>
    {
        if (!string.Equals(state.PageId, _pageId, StringComparison.Ordinal))
            return;

        _pickerActive = state.Enabled;
        UpdateButtons();
    });

    private void OnDeviceStateChanged(BrowserDeviceModeStateChangedEvent state) => UiThreadBridge.Post(() =>
    {
        if (!string.Equals(state.PageId, _pageId, StringComparison.Ordinal))
            return;

        _deviceActive = state.Enabled;
        UpdateButtons();
    });

    private void TogglePicker()
    {
        if (_pageId is null)
            return;

        _eventBus.Publish(new ElementPickerToggleRequestedEvent(_pageId, !_pickerActive));
    }

    private void ToggleDeviceMode()
    {
        if (_pageId is null)
            return;

        _eventBus.Publish(new BrowserDeviceModeToggleRequestedEvent(_pageId, !_deviceActive));
    }

    private void UpdateButtons()
    {
        var enabled = _pageId is not null;
        _pickerButton.IsEnabled = enabled;
        _deviceButton.IsEnabled = enabled;
        _pickerButton.Background = _pickerActive ? new SolidColorBrush(Color.Parse("#DDEEFF")) : Brushes.Transparent;
        _deviceButton.Background = _deviceActive ? new SolidColorBrush(Color.Parse("#DDEEFF")) : Brushes.Transparent;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _activeTabSubscription.Dispose();
        _pickerStateSubscription.Dispose();
        _deviceStateSubscription.Dispose();
    }
}
