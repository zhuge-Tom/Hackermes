using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hackermes.Base.Mvvm;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.Inspector.ViewModels;

public sealed record TrafficComparerSourceItem(string Reference, string Label, string Kind);
public sealed record TrafficComparerSessionItem(
    string Id, string Name, string LeftReference, string RightReference,
    string Summary, string Result, int Revision, DateTimeOffset UpdatedAt);

public interface ITrafficComparerWorkbenchService
{
    IReadOnlyList<TrafficComparerSessionItem> Sessions { get; }
    IReadOnlyList<TrafficComparerSourceItem> Sources { get; }
    event Action? Changed;
    Task<string> CompareAsync(string leftPacketId, string rightPacketId, string side, CancellationToken cancellationToken);
    Task<string> CompareSourcesAsync(string leftReference, string rightReference, CancellationToken cancellationToken);
    Task<TrafficComparerSessionItem> CreateSessionAsync(string name, string leftReference, string rightReference, CancellationToken cancellationToken);
    Task<TrafficComparerSessionItem> RenameSessionAsync(string id, string name, CancellationToken cancellationToken);
    Task<TrafficComparerSessionItem> RecalculateSessionAsync(string id, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string id, CancellationToken cancellationToken);
}

public partial class TrafficComparerViewModel : ViewModelBase
{
    private readonly ITrafficComparerWorkbenchService _service;

    public TrafficComparerViewModel(ITrafficComparerWorkbenchService service)
    {
        _service = service;
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    public ObservableCollection<TrafficComparerSessionItem> Sessions { get; } = [];
    public ObservableCollection<TrafficComparerSourceItem> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameSessionCommand), nameof(RecalculateSessionCommand), nameof(DeleteSessionCommand))]
    private TrafficComparerSessionItem? _selectedSession;
    [ObservableProperty] private TrafficComparerSourceItem? _selectedLeftSource;
    [ObservableProperty] private TrafficComparerSourceItem? _selectedRightSource;
    [ObservableProperty] private string _leftSourceReference = string.Empty;
    [ObservableProperty] private string _rightSourceReference = string.Empty;
    [ObservableProperty] private string _sessionName = string.Empty;
    [ObservableProperty] private string _result = "Select current Traffic or Repeater sources, or enter source references.";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameSessionCommand), nameof(RecalculateSessionCommand), nameof(DeleteSessionCommand))]
    private bool _isBusy;

    public bool HasSelectedSession => SelectedSession is not null && !IsBusy;

    partial void OnSelectedSessionChanged(TrafficComparerSessionItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedSession));
        if (value is null) return;
        SessionName = value.Name;
        LeftSourceReference = value.LeftReference;
        RightSourceReference = value.RightReference;
        Result = value.Result;
    }

    partial void OnSelectedLeftSourceChanged(TrafficComparerSourceItem? value)
    {
        if (value is not null) LeftSourceReference = value.Reference;
    }

    partial void OnSelectedRightSourceChanged(TrafficComparerSourceItem? value)
    {
        if (value is not null) RightSourceReference = value.Reference;
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasSelectedSession));

    [RelayCommand]
    private Task CompareAsync() => ExecuteAsync(async ct =>
        Result = await _service.CompareSourcesAsync(LeftSourceReference.Trim(), RightSourceReference.Trim(), ct));

    [RelayCommand]
    private Task CreateSessionAsync() => ExecuteAsync(async ct =>
    {
        var created = await _service.CreateSessionAsync(SessionName.Trim(), LeftSourceReference.Trim(), RightSourceReference.Trim(), ct);
        Refresh();
        SelectedSession = FindSession(created.Id);
        Result = created.Result;
    });

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private Task RenameSessionAsync() => ExecuteAsync(async ct =>
    {
        var updated = await _service.RenameSessionAsync(SelectedSession!.Id, SessionName.Trim(), ct);
        Refresh();
        SelectedSession = FindSession(updated.Id);
        Result = updated.Result;
    });

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private Task RecalculateSessionAsync() => ExecuteAsync(async ct =>
    {
        var updated = await _service.RecalculateSessionAsync(SelectedSession!.Id, ct);
        Refresh();
        SelectedSession = FindSession(updated.Id);
        Result = updated.Result;
    });

    [RelayCommand(CanExecute = nameof(HasSelectedSession))]
    private Task DeleteSessionAsync() => ExecuteAsync(async ct =>
    {
        var id = SelectedSession!.Id;
        await _service.DeleteSessionAsync(id, ct);
        Refresh();
        SelectedSession = null;
        Result = $"Comparison session '{id}' deleted.";
    });

    [RelayCommand]
    private void Refresh()
    {
        var selectedId = SelectedSession?.Id;
        Sessions.Clear();
        foreach (var session in _service.Sessions) Sessions.Add(session);
        Sources.Clear();
        foreach (var source in _service.Sources) Sources.Add(source);
        if (selectedId is not null) SelectedSession = FindSession(selectedId);
    }

    private void OnServiceChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private TrafficComparerSessionItem? FindSession(string id)
    {
        foreach (var session in Sessions)
            if (string.Equals(session.Id, id, StringComparison.Ordinal)) return session;
        return null;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        try { await action(CancellationToken.None); }
        catch (Exception ex) { Result = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    protected override void OnDispose() => _service.Changed -= OnServiceChanged;
}
