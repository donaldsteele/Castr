using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Castr.Gui.Services;

/// <summary>Thin wrappers over Avalonia's <see cref="IStorageProvider"/> returning plain local paths.</summary>
public static class StoragePickers
{
    public static async Task<string?> PickFileAsync(Visual owner)
    {
        var provider = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null)
            return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a file to send",
            AllowMultiple = false,
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFolderAsync(Visual owner)
    {
        var provider = TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null)
            return null;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a destination folder",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
