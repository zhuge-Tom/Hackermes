using Hackermes.Browser.Services;
using Hackermes.Browser.ViewModels;
using System;
using System.IO;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class BrowserHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hackermes-browser-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void History_is_persistent_deduplicated_and_clearable()
    {
        var path = Path.Combine(_root, "history.json");
        var store = new BrowserHistoryStore(path);
        store.Record("https://first.example/", "First");
        store.Record("https://second.example/", "Second");
        store.Record("https://first.example/", "First updated");

        var loaded = new BrowserHistoryStore(path);

        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal("https://first.example/", loaded.Entries[0].Url);
        Assert.Equal("First updated", loaded.Entries[0].Title);
        loaded.Clear();
        Assert.Empty(new BrowserHistoryStore(path).Entries);
    }

    [Fact]
    public void Selecting_history_navigates_to_a_previously_closed_page()
    {
        var store = new BrowserHistoryStore(Path.Combine(_root, "history.json"));
        store.Record("https://closed.example/path", "Closed page");
        var viewModel = new BrowserTabViewModel("page-new", "about:blank", store);
        string? navigated = null;
        viewModel.NavigateRequested += value => navigated = value;

        viewModel.SelectedHistory = Assert.Single(viewModel.History);

        Assert.Equal("https://closed.example/path", navigated);
        Assert.Equal("https://closed.example/path", viewModel.AddressText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
