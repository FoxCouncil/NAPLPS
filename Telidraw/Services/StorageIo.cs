// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Platform.Storage;

namespace Telidraw.Services;

/// <summary>
/// Bridges the app's path-based file code with storage items that have no filesystem
/// path. On desktop every picked file resolves to its local path and nothing changes.
/// In the browser the picker hands back virtual items: reads stage the content into the
/// WASM in-memory filesystem (so Bitmap(path), importers and FromFile keep working),
/// and writes stream back through the item, which the browser turns into a download or
/// a File System Access write.
/// </summary>
public static class StorageIo
{
    /// <summary>A local filesystem path whose content is the storage file's content.</summary>
    public static async Task<string> GetReadablePathAsync(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } local)
        {
            return local;
        }

        var staged = System.IO.Path.Combine(System.IO.Path.GetTempPath(), file.Name);

        await using var source = await file.OpenReadAsync();
        await using var destination = System.IO.File.Create(staged);
        await source.CopyToAsync(destination);

        return staged;
    }

    public static async Task WriteBytesAsync(IStorageFile file, byte[] bytes)
    {
        if (file.TryGetLocalPath() is { } local)
        {
            await System.IO.File.WriteAllBytesAsync(local, bytes);
            return;
        }

        await using var destination = await file.OpenWriteAsync();
        await destination.WriteAsync(bytes);
    }

    public static async Task WriteTextAsync(IStorageFile file, string text)
    {
        await WriteBytesAsync(file, System.Text.Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Copies an already-written local (or staged) file into the storage item.</summary>
    public static async Task WriteFromPathAsync(IStorageFile file, string sourcePath)
    {
        await using var source = System.IO.File.OpenRead(sourcePath);
        await using var destination = await file.OpenWriteAsync();
        await source.CopyToAsync(destination);
    }
}
