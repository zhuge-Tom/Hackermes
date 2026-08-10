using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using Hackermes.Inspector.Services;
using Hackermes.Platform.Registries;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.ViewModels;

public abstract partial class PageInspectorViewModelBase : ViewModelBase
{
    protected readonly PageInspectionService Inspection;
    private CancellationTokenSource? _operation;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Select a browser page, then refresh.";

    protected PageInspectorViewModelBase(PageInspectionService inspection) => Inspection = inspection;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        IsBusy = true;
        try { await LoadAsync(_operation.Token); }
        catch (OperationCanceledException) { Status = "Refresh cancelled."; }
        catch (Exception exception) { Status = $"Refresh failed: {exception.Message}"; }
        finally { IsBusy = false; }
    }

    protected abstract Task LoadAsync(CancellationToken cancellationToken);
    protected override void OnDispose() { _operation?.Cancel(); _operation?.Dispose(); }
}

public sealed partial class DomInspectorViewModel : PageInspectorViewModelBase, ITabActivationAware
{
    private readonly PageInspectionService _inspection;
    private readonly Dictionary<string, DomTreeNodeViewModel> _nodesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DomTreeNodeViewModel> _nodesByPath = new(StringComparer.Ordinal);
    public ObservableCollection<DomTreeNodeViewModel> RootItems { get; } = [];
    public ObservableCollection<DomTreeNodeViewModel> BreadcrumbItems { get; } = [];
    public ObservableCollection<DomPropertyItem> SelectedAttributes { get; } = [];
    public ObservableCollection<DomPropertyItem> SelectedComputedStyles { get; } = [];
    public ObservableCollection<DomCssRuleItem> MatchedCssRules { get; } = [];
    private int _selectionVersion;
    private bool _nextSelectionScroll = true;
    private DomTreeNodeViewModel? _pageHoveredItem;

    public event Action<DomTreeNodeViewModel>? NodeRevealRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedResourceCommand), nameof(ApplyCssCommand))]
    private DomTreeNodeViewModel? _selectedItem;

    [ObservableProperty] private string _selectedNodeTitle = "Select an element from the DOM tree.";
    [ObservableProperty] private string _selectedNodePath = "";
    [ObservableProperty] private string _editableCss = string.Empty;
    [ObservableProperty] private DomCssRuleItem? _selectedCssRule;
    public string SelectedResourceUrl => SelectedItem?.Item.ResourceUrl ?? "No linked src/href resource on this element.";

    public DomInspectorViewModel(PageInspectionService inspection) : base(inspection)
    {
        _inspection = inspection;
        _inspection.PickerMessageReceived += OnPickerMessageReceived;
        _inspection.PageNavigated += OnPageNavigated;
    }

    /// <summary>
    /// A DOM snapshot is deliberately refreshed whenever the tab becomes visible. DOM nodes are
    /// live page objects, so restoring an old hidden-tab snapshot would be misleading.
    /// </summary>
    public void OnTabActivated() => _ = ActivateAsync();

    public Task ActivateAsync() => RefreshCommand.ExecuteAsync(null);

    partial void OnSelectedItemChanged(DomTreeNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedResourceUrl));
        var scrollTo = _nextSelectionScroll;
        _nextSelectionScroll = true;
        if (value is not null) _ = InspectSelectedAsync(value, Interlocked.Increment(ref _selectionVersion), scrollTo);
    }

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var values = await Inspection.ReadDomAsync(cancellationToken);
        RootItems.Clear();
        _nodesByKey.Clear();
        _nodesByPath.Clear();
        var ancestors = new Stack<DomTreeNodeViewModel>();
        foreach (var value in values)
        {
            var node = new DomTreeNodeViewModel(value);
            while (ancestors.Count > value.Depth) ancestors.Pop();
            if (ancestors.Count == 0) RootItems.Add(node);
            else
            {
                node.Parent = ancestors.Peek();
                ancestors.Peek().Children.Add(node);
            }
            ancestors.Push(node);
            _nodesByPath[value.Path] = node;
            if (!string.IsNullOrWhiteSpace(value.NodeKey)) _nodesByKey[value.NodeKey] = node;
        }
        SelectedItem = null;
        BreadcrumbItems.Clear();
        SelectedAttributes.Clear();
        SelectedComputedStyles.Clear();
        MatchedCssRules.Clear();
        SelectedNodeTitle = "Select an element from the DOM tree.";
        SelectedNodePath = "";
        EditableCss = string.Empty;
        SelectedCssRule = null;
        Status = $"{values.Count} elements (bounded to {PageInspectionService.MaximumItems}, depth {PageInspectionService.MaximumDomDepth}). Select one to highlight it on the page.";
    }

    private async Task InspectSelectedAsync(DomTreeNodeViewModel node, int selectionVersion, bool scrollTo)
    {
        try
        {
            var detailsTask = Inspection.ReadDomNodeDetailsAsync(node.Item.Path, node.Item.NodeKey, CancellationToken.None);
            var highlightedTask = scrollTo
                ? Inspection.HighlightDomElementAsync(node.Item.Path, node.Item.NodeKey, CancellationToken.None)
                : Task.FromResult(true);
            await Task.WhenAll(detailsTask, highlightedTask);
            if (selectionVersion != _selectionVersion || !ReferenceEquals(node, SelectedItem)) return;

            SetBreadcrumb(node);
            var details = await detailsTask;
            if (details is not null) PopulateDetails(details);
            Status = !scrollTo
                ? $"Previewing {node.Item.Display}."
                : highlightedTask.Result
                    ? $"Selected {node.Item.Display}; the page was scrolled to and highlighted."
                    : "The selected element no longer exists; refresh the tree.";
        }
        catch (Exception exception) { Status = $"Highlight failed: {exception.Message}"; }
    }

    private void SetBreadcrumb(DomTreeNodeViewModel node)
    {
        var reversed = new Stack<DomTreeNodeViewModel>();
        for (var current = node; current is not null; current = current.Parent) reversed.Push(current);
        BreadcrumbItems.Clear();
        while (reversed.Count > 0) BreadcrumbItems.Add(reversed.Pop());
    }

    private void PopulateDetails(DomNodeDetails details)
    {
        SelectedAttributes.Clear();
        SelectedComputedStyles.Clear();
        MatchedCssRules.Clear();
        foreach (var attribute in details.Attributes) SelectedAttributes.Add(attribute);
        foreach (var style in details.ComputedStyles) SelectedComputedStyles.Add(style);
        foreach (var rule in details.MatchedRules ?? []) MatchedCssRules.Add(rule);
        SelectedNodeTitle = details.Selector;
        SelectedNodePath = details.Path;
        SelectedCssRule = MatchedCssRules.FirstOrDefault();
        EditableCss = SelectedCssRule?.CssText ?? string.Empty;
        OnPropertyChanged(nameof(SelectedResourceUrl));
    }

    partial void OnSelectedCssRuleChanged(DomCssRuleItem? value) => EditableCss = value?.CssText ?? string.Empty;

    private async void OnPickerMessageReceived(DomPickerMessage message)
    {
        if (!string.Equals(message.PageId, Inspection.ActivePageId, StringComparison.Ordinal)) return;
        var node = !string.IsNullOrWhiteSpace(message.NodeKey) && _nodesByKey.TryGetValue(message.NodeKey, out var keyed)
            ? keyed
            : _nodesByPath.TryGetValue(message.Path, out var pathNode) ? pathNode : null;
        if (node is null)
        {
            try
            {
                await LoadAsync(CancellationToken.None);
                node = !string.IsNullOrWhiteSpace(message.NodeKey) && _nodesByKey.TryGetValue(message.NodeKey, out keyed)
                    ? keyed
                    : _nodesByPath.TryGetValue(message.Path, out pathNode) ? pathNode : null;
            }
            catch (Exception exception)
            {
                Status = $"Picker refresh failed: {exception.Message}";
                return;
            }
        }
        if (node is null)
        {
            Status = $"Picked {message.Selector}, but it changed before the DOM tree could refresh.";
            return;
        }

        ExpandAncestors(node);
        NodeRevealRequested?.Invoke(node);
        if (message.Kind == "hover")
        {
            if (!ReferenceEquals(_pageHoveredItem, node))
            {
                if (_pageHoveredItem is not null) _pageHoveredItem.IsPageHovered = false;
                _pageHoveredItem = node;
                node.IsPageHovered = true;
            }
            Status = $"Hovering {node.Item.Display}; click the page to select it.";
            return;
        }

        ClearPageHover();
        SelectNode(node, preview: false);
    }

    public async void PreviewNode(DomTreeNodeViewModel node)
    {
        try
        {
            await Inspection.PreviewDomElementAsync(node.Item.Path, node.Item.NodeKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Status = $"Preview failed: {exception.Message}";
        }
    }

    private void SelectNode(DomTreeNodeViewModel node, bool preview)
    {
        ExpandAncestors(node);
        if (ReferenceEquals(SelectedItem, node))
        {
            _ = InspectSelectedAsync(node, Interlocked.Increment(ref _selectionVersion), scrollTo: !preview);
            return;
        }
        _nextSelectionScroll = !preview;
        SelectedItem = node;
    }

    private static void ExpandAncestors(DomTreeNodeViewModel node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
    }

    private void ClearPageHover()
    {
        if (_pageHoveredItem is null) return;
        _pageHoveredItem.IsPageHovered = false;
        _pageHoveredItem = null;
    }

    private void OnPageNavigated(string pageId)
    {
        if (!string.Equals(pageId, Inspection.ActivePageId, StringComparison.Ordinal)) return;
        Interlocked.Increment(ref _selectionVersion);
        ClearPageHover();
        RootItems.Clear();
        BreadcrumbItems.Clear();
        SelectedAttributes.Clear();
        SelectedComputedStyles.Clear();
        MatchedCssRules.Clear();
        SelectedItem = null;
        SelectedNodeTitle = "The page changed. Reopen this tab or press Refresh to load the latest tree.";
        SelectedNodePath = string.Empty;
        EditableCss = string.Empty;
        SelectedCssRule = null;
        Status = "Page navigation detected; stale DOM nodes were discarded.";
    }

    private bool HasSelectedElement() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedElement))]
    private async Task ApplyCssAsync()
    {
        if (SelectedItem is not { } node) return;
        IsBusy = true;
        try
        {
            var editedStylesheetRule = SelectedCssRule is { IsInline: false, RuleKey: { } };
            var result = SelectedCssRule is { IsInline: false, RuleKey: { } ruleKey }
                ? await Inspection.ApplyCssRuleAsync(ruleKey, EditableCss, CancellationToken.None)
                : await Inspection.ApplyInlineCssAsync(node.Item.Path, node.Item.NodeKey, EditableCss, CancellationToken.None);
            if (!result.Applied)
            {
                Status = $"CSS was not applied: {result.Error}";
                return;
            }

            EditableCss = result.StyleText ?? string.Empty;
            var details = await Inspection.ReadDomNodeDetailsAsync(node.Item.Path, node.Item.NodeKey, CancellationToken.None);
            if (details is not null) PopulateDetails(details);
            Status = editedStylesheetRule
                ? "The selected stylesheet rule was applied. It may affect every element matching the selector."
                : "Inline CSS was applied to the selected element.";
        }
        catch (Exception exception) { Status = $"CSS edit failed: {exception.Message}"; }
        finally { IsBusy = false; }
    }

    private bool HasSelectedResource() => !string.IsNullOrWhiteSpace(SelectedItem?.Item.ResourceUrl);

    [RelayCommand(CanExecute = nameof(HasSelectedResource))]
    private void OpenSelectedResource()
    {
        if (SelectedItem?.Item.ResourceUrl is not { } url) return;
        try
        {
            Inspection.OpenResourceInBrowser(url);
            Status = "Opened the selected element resource in a new Hackermes browser tab.";
        }
        catch (Exception exception) { Status = $"Open resource failed: {exception.Message}"; }
    }

    protected override void OnDispose()
    {
        _inspection.PickerMessageReceived -= OnPickerMessageReceived;
        _inspection.PageNavigated -= OnPageNavigated;
        base.OnDispose();
    }
}

public sealed partial class DomTreeNodeViewModel : ObservableObject
{
    public DomTreeNodeViewModel(DomNodeItem item)
    {
        Item = item;
        IsExpanded = item.Depth < 2;
    }

    public DomNodeItem Item { get; }
    public DomTreeNodeViewModel? Parent { get; set; }
    public ObservableCollection<DomTreeNodeViewModel> Children { get; } = [];
    public string Display => Item.Display;
    public string Preview => string.IsNullOrWhiteSpace(Item.Text) ? string.Empty : Item.Text;
    public string ChildSummary => Item.ChildCount == 0 ? string.Empty : $"{Item.ChildCount} child{(Item.ChildCount == 1 ? string.Empty : "ren")}";
    public string PageHoverMarker => IsPageHovered ? "▶" : string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageHoverMarker))]
    private bool _isPageHovered;
}

public sealed partial class StorageInspectorViewModel(PageInspectionService inspection) : PageInspectorViewModelBase(inspection)
{
    public ObservableCollection<PageStorageItem> Items { get; } = [];
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var values = await Inspection.ReadStorageAsync(cancellationToken);
        Items.Clear(); foreach (var value in values) Items.Add(value);
        Status = $"{Items.Count} local/session storage and script-visible cookie entries.";
    }
}

public sealed partial class ResourceExplorerViewModel(PageInspectionService inspection) : PageInspectorViewModelBase(inspection)
{
    public ObservableCollection<PageResourceItem> Items { get; } = [];

    public event Action<string>? CopyRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedResourceCommand), nameof(CopySelectedResourceCommand), nameof(HighlightSelectedResourceCommand))]
    private PageResourceItem? _selectedItem;

    public string SelectedSummary => SelectedItem is null
        ? "Select a resource to inspect its URL and page-element source."
        : $"{SelectedItem.Url} | {SelectedItem.Duration:0.##} ms | {SelectedItem.ElementSummary}";

    partial void OnSelectedItemChanged(PageResourceItem? value)
    {
        OnPropertyChanged(nameof(SelectedSummary));
        if (value is not null) _ = HighlightSelectedResourceAsync();
    }

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var values = await Inspection.ReadResourcesAsync(cancellationToken);
        Items.Clear(); foreach (var value in values) Items.Add(value);
        SelectedItem = null;
        Status = $"{Items.Count} page resources (latest {PageInspectionService.MaximumItems}).";
    }

    private bool HasSelectedItem() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedItem))]
    private void OpenSelectedResource()
    {
        if (SelectedItem is not { } item) return;
        try
        {
            Inspection.OpenResourceInBrowser(item.Url);
            Status = "Opened resource in a new Hackermes browser tab.";
        }
        catch (Exception exception) { Status = $"Open failed: {exception.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedItem))]
    private void CopySelectedResource()
    {
        if (SelectedItem is { } item) CopyRequested?.Invoke(item.Url);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedItem))]
    private async Task HighlightSelectedResourceAsync()
    {
        if (SelectedItem is not { } item) return;
        IsBusy = true;
        try
        {
            var count = await Inspection.HighlightResourceElementsAsync(item.Url, CancellationToken.None);
            Status = count == 0
                ? "This resource has no matching DOM element (for example, fetch/XHR or a browser-internal resource)."
                : $"Highlighted {count} matching page element(s) for 1.8 seconds.";
        }
        catch (Exception exception) { Status = $"Highlight failed: {exception.Message}"; }
        finally { IsBusy = false; }
    }
}
