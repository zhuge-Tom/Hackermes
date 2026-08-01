using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Hookmes.Inspector.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.Inspector.Views;

internal static class InspectorStorageDialogs
{
    public static InspectorFileDialogDelegates Create(Control owner) => new(
        (request, cancellationToken) => OpenAsync(owner, request, cancellationToken),
        (request, cancellationToken) => SaveAsync(owner, request, cancellationToken),
        (message, cancellationToken) => ConfirmAsync(owner, message, cancellationToken));

    private static async Task<string?> OpenAsync(Control owner, InspectorFileDialogRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner) ?? throw new InvalidOperationException("File picker requires an attached TopLevel.");
        var startLocation = await GetSuggestedStartLocationAsync(topLevel.StorageProvider, request.SuggestedPath);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            FileTypeFilter = ToPickerTypes(request),
            SuggestedStartLocation = startLocation
        });
        ct.ThrowIfCancellationRequested();
        return files.Count == 0 ? null : ToLocalPath(files[0]);
    }

    private static async Task<string?> SaveAsync(Control owner, InspectorFileDialogRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var topLevel = TopLevel.GetTopLevel(owner) ?? throw new InvalidOperationException("File picker requires an attached TopLevel.");
        var startLocation = await GetSuggestedStartLocationAsync(topLevel.StorageProvider, request.SuggestedPath);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = request.Title,
            SuggestedFileName = request.SuggestedFileName,
            SuggestedStartLocation = startLocation,
            FileTypeChoices = ToPickerTypes(request),
            ShowOverwritePrompt = false // Hookmes uses one consistent in-app confirmation below.
        });
        ct.ThrowIfCancellationRequested();
        if (file is null) return null;
        var path = ToLocalPath(file);
        if (File.Exists(path) && !await ConfirmAsync(owner,
                $"The file already exists. Replace it?{Environment.NewLine}{path}", ct)) return null;
        return path;
    }

    private static async Task<IStorageFolder?> GetSuggestedStartLocationAsync(IStorageProvider provider, string suggestedPath)
    {
        if (string.IsNullOrWhiteSpace(suggestedPath)) return null;
        var directory = Directory.Exists(suggestedPath)
            ? suggestedPath
            : Path.GetDirectoryName(Path.GetFullPath(suggestedPath));
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : await provider.TryGetFolderFromPathAsync(directory);
    }

    private static IReadOnlyList<FilePickerFileType> ToPickerTypes(InspectorFileDialogRequest request) =>
        request.FileTypes.Select(type => new FilePickerFileType(type.Name) { Patterns = type.Patterns }).ToArray();

    private static string ToLocalPath(IStorageItem item)
    {
        if (!item.Path.IsFile) throw new InvalidOperationException("Only local filesystem paths are supported.");
        return item.Path.LocalPath;
    }

    private static async Task<bool> ConfirmAsync(Control owner, string message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (TopLevel.GetTopLevel(owner) is not Window parent) return false;
        var dialog = new Window
        {
            Title = "Confirm operation",
            Width = 440,
            Height = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var confirm = new Button { Content = "Confirm", MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } }
            }
        };
        var result = await dialog.ShowDialog<bool>(parent);
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
