using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.ViewModels;

public sealed record RepeaterDraftItem(
    string Id, string Name, string RequestText, int Revision, int SendCount,
    string LatestStatus, string LatestMetrics, string LatestResponse,
    IReadOnlyList<RepeaterRoundItem> History);

public sealed record RepeaterRoundItem(
    string DraftId, string DraftName, string ResultId, int Sequence, string Status,
    string Metrics, string RequestText, string ResponseText, bool HasResponse)
{
    public string Label => $"{DraftName} · #{Sequence} · {Status}";
}

public interface IRepeaterWorkbenchService
{
    IReadOnlyList<RepeaterDraftItem> Drafts { get; }
    event Action? RepeaterChanged;
    Task<RepeaterDraftItem> SendAsync(
        string id, string name, string request, TimeSpan timeout, CancellationToken cancellationToken);
    Task DeleteAsync(string id, CancellationToken cancellationToken);
    Task ClearHistoryAsync(string id, CancellationToken cancellationToken);
    Task<string> CompareRoundsAsync(string leftDraftId, string leftResultId,
        string rightDraftId, string rightResultId, string side, CancellationToken cancellationToken);
    Task<string> SaveRoundComparisonAsync(string name, string leftDraftId, string leftResultId,
        string rightDraftId, string rightResultId, string side, CancellationToken cancellationToken);
}

public partial class RepeaterWorkbenchViewModel : ViewModelBase
{
    private readonly IRepeaterWorkbenchService _service;
    private CancellationTokenSource? _sendCancellation;

    public RepeaterWorkbenchViewModel(IRepeaterWorkbenchService service)
    {
        _service = service;
        _service.RepeaterChanged += Refresh;
        Refresh();
    }

    public ObservableCollection<RepeaterDraftItem> Drafts { get; } = [];
    public ObservableCollection<RepeaterRoundItem> Rounds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DeleteCommand), nameof(ClearHistoryCommand))]
    private RepeaterDraftItem? _selected;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _requestEditor = string.Empty;
    [ObservableProperty] private string _responseViewer = string.Empty;
    [ObservableProperty] private RepeaterRoundItem? _viewedRound;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareRoundsCommand), nameof(SaveRoundComparisonCommand))]
    private RepeaterRoundItem? _leftRound;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareRoundsCommand), nameof(SaveRoundComparisonCommand))]
    private RepeaterRoundItem? _rightRound;
    [ObservableProperty] private string _comparisonSide = "response";
    [ObservableProperty] private string _comparisonName = "Repeater comparison";
    [ObservableProperty] private string _comparisonResult = "Select any two rounds to compare.";
    [ObservableProperty] private string _status = "Send a packet here from the Data Packet workbench.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DeleteCommand), nameof(ClearHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompareRoundsCommand), nameof(SaveRoundComparisonCommand), nameof(CancelSendCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSendCommand))]
    private bool _isSending;
    [ObservableProperty] private decimal _timeoutSeconds = 30;

    public bool CanOperate => Selected is not null && !IsBusy;
    public bool CanCompareRounds => LeftRound is not null && RightRound is not null && !IsBusy;
    public bool CanCancelSend => IsBusy && IsSending;

    partial void OnSelectedChanged(RepeaterDraftItem? value)
    {
        Name = value?.Name ?? string.Empty;
        RequestEditor = value?.RequestText ?? string.Empty;
        ResponseViewer = value?.LatestResponse ?? string.Empty;
        Status = value is null ? "Select a draft." : $"{value.LatestStatus}  {value.LatestMetrics}";
        OnPropertyChanged(nameof(CanOperate));
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanOperate));
    partial void OnViewedRoundChanged(RepeaterRoundItem? value)
    {
        if (value is null) return;
        RequestEditor = value.RequestText;
        ResponseViewer = value.ResponseText;
        Status = value.Metrics;
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task SendAsync()
    {
        IsBusy = true;
        IsSending = true;
        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        try
        {
            var timeout = TimeSpan.FromSeconds((double)TimeoutSeconds);
            var result = await _service.SendAsync(Selected!.Id, Name, RequestEditor, timeout, cancellation.Token);
            ResponseViewer = result.LatestResponse;
            Status = $"{result.LatestStatus}  {result.LatestMetrics}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "Cancelled  The send was cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_sendCancellation, cancellation))
                _sendCancellation = null;
            cancellation.Dispose();
            IsSending = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelSend))]
    private void CancelSend()
    {
        Status = "Cancelling send...";
        // Cancellation callbacks may resume SendAsync on another thread before
        // Cancel returns. Publish the transient state first so the terminal
        // Cancelled/result state written by SendAsync can never be overwritten.
        _sendCancellation?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task DeleteAsync() => ExecuteAsync(ct => _service.DeleteAsync(Selected!.Id, ct));

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ClearHistoryAsync() => ExecuteAsync(ct => _service.ClearHistoryAsync(Selected!.Id, ct));

    [RelayCommand(CanExecute = nameof(CanCompareRounds))]
    private Task CompareRoundsAsync() => ExecuteAsync(async ct =>
    {
        ComparisonResult = await _service.CompareRoundsAsync(
            LeftRound!.DraftId, LeftRound.ResultId, RightRound!.DraftId, RightRound.ResultId,
            ComparisonSide.Trim(), ct);
    });

    [RelayCommand(CanExecute = nameof(CanCompareRounds))]
    private Task SaveRoundComparisonAsync() => ExecuteAsync(async ct =>
    {
        ComparisonResult = await _service.SaveRoundComparisonAsync(ComparisonName.Trim(),
            LeftRound!.DraftId, LeftRound.ResultId, RightRound!.DraftId, RightRound.ResultId,
            ComparisonSide.Trim(), ct);
    });

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
        var viewedId = ViewedRound?.ResultId;
        var leftId = LeftRound?.ResultId;
        var rightId = RightRound?.ResultId;
        Rounds.Clear();
        foreach (var round in Drafts.SelectMany(draft => draft.History)) Rounds.Add(round);
        Selected = selectedId is null ? Drafts.FirstOrDefault() : Drafts.FirstOrDefault(x => x.Id == selectedId);
        ViewedRound = Rounds.FirstOrDefault(x => x.ResultId == viewedId) ?? Rounds.FirstOrDefault();
        LeftRound = Rounds.FirstOrDefault(x => x.ResultId == leftId) ?? Rounds.FirstOrDefault();
        RightRound = Rounds.FirstOrDefault(x => x.ResultId == rightId) ?? Rounds.Skip(1).FirstOrDefault();
    }

    protected override void OnDispose()
    {
        _service.RepeaterChanged -= Refresh;
        _sendCancellation?.Cancel();
        _sendCancellation?.Dispose();
        _sendCancellation = null;
    }
}
