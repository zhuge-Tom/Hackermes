using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hookmes.Base.Mvvm;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.ViewModels;

public interface ITrafficComparerWorkbenchService
{
    Task<string> CompareAsync(string leftPacketId, string rightPacketId, string side, CancellationToken cancellationToken);
}

public partial class TrafficComparerViewModel : ViewModelBase
{
    private readonly ITrafficComparerWorkbenchService _service;
    public TrafficComparerViewModel(ITrafficComparerWorkbenchService service) => _service = service;

    [ObservableProperty] private string _leftPacketId = string.Empty;
    [ObservableProperty] private string _rightPacketId = string.Empty;
    [ObservableProperty] private string _side = "request";
    [ObservableProperty] private string _result = "Enter two packet ids to compare start line, headers and body.";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task CompareAsync()
    {
        IsBusy = true;
        try { Result = await _service.CompareAsync(LeftPacketId.Trim(), RightPacketId.Trim(), Side.Trim(), CancellationToken.None); }
        catch (Exception ex) { Result = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
