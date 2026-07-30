using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hookmes.Base.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.ViewModels;

public sealed record RepeaterDraftItem(
    string Id, string Name, string RequestText, int Revision, int SendCount,
    string LatestStatus, string LatestMetrics, string LatestResponse);

public interface IRepeaterWorkbenchService
{
    IReadOnlyList<RepeaterDraftItem> Drafts { get; }
    event Action? RepeaterChanged;
    Task<RepeaterDraftItem> SendAsync(string id, string name, string request, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task ClearHistoryAsync(string id, CancellationToken cancellationToken);
}

public partial class RepeaterWorkbenchViewModel : ViewModelBase
{
    private readonly IRepeaterWorkbenchService _service;

    public RepeaterWorkbenchViewModel(IRepeaterWorkbenchService service)
    {
        _service = service;
        _service.RepeaterChanged += Refresh;
        Refresh();
    }

    public ObservableCollection<RepeaterDraftItem> Drafts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DeleteCommand), nameof(ClearHistoryCommand))]
    private RepeaterDraftItem? _selected;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _requestEditor = string.Empty;
    [ObservableProperty] private string _responseViewer = string.Empty;
    [ObservableProperty] private string _status = "Send a packet here from the Data Packet workbench.";
    [ObservableProperty] private bool _isBusy;

    public bool CanOperate => Selected is not null && !IsBusy;

    partial void OnSelectedChanged(RepeaterDraftItem? value)
    {
        Name = value?.Name ?? string.Empty;
        RequestEditor = value?.RequestText ?? string.Empty;
        ResponseViewer = value?.LatestResponse ?? string.Empty;
        Status = value is null ? "Select a draft." : $"{value.LatestStatus}  {value.LatestMetrics}";
        OnPropertyChanged(nameof(CanOperate));
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanOperate));

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SendAsync() => ExecuteAsync(async ct =>
    {
        var result = await _service.SendAsync(Selected!.Id, Name, RequestEditor, ct);
        ResponseViewer = result.LatestResponse;
        Status = $"{result.LatestStatus}  {result.LatestMetrics}";
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task DeleteAsync() => ExecuteAsync(ct => _service.DeleteAsync(Selected!.Id, ct));

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ClearHistoryAsync() => ExecuteAsync(ct => _service.ClearHistoryAsync(Selected!.Id, ct));

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        try { await action(CancellationToken.None); }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        var selectedId = Selected?.Id;
        Drafts.Clear();
        foreach (var draft in _service.Drafts) Drafts.Add(draft);
        Selected = selectedId is null ? Drafts.FirstOrDefault() : Drafts.FirstOrDefault(x => x.Id == selectedId);
    }

    protected override void OnDispose() => _service.RepeaterChanged -= Refresh;
}
