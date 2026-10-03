using Microsoft.JSInterop;

namespace VolumeScope.Web.State;

/// <summary>ブラウザーとのやりとり（ファイルの受け取り・保存・キー操作）。ファイルはブラウザーの中だけで扱う</summary>
public sealed class BrowserIo(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? io;
    private IJSObjectReference? files;

    private async ValueTask<IJSObjectReference> Io() => io ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/io.js");

    private async ValueTask<IJSObjectReference> Files() => files ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/files.js");

    public async ValueTask InitFilesAsync(object dotnetRef, string dropId) => await (await Files()).InvokeVoidAsync("init", dotnetRef, dropId);

    public async ValueTask<string> FileNameAsync(int index) => await (await Files()).InvokeAsync<string>("name", index);

    public async ValueTask<byte[]?> ReadFileAsync(int index) => await (await Files()).InvokeAsync<byte[]?>("read", index);

    public async ValueTask ReleaseFilesAsync() => await (await Files()).InvokeVoidAsync("release");

    public async ValueTask DownloadAsync(string name, string mime, byte[] bytes) => await (await Io()).InvokeVoidAsync("download", name, mime, bytes);

    public async ValueTask<bool> SaveScreensAsync(string selector, string name) => await (await Io()).InvokeAsync<bool>("saveScreens", selector, name);

    public async ValueTask ListenKeysAsync(object dotnetRef) => await (await Io()).InvokeVoidAsync("listenKeys", dotnetRef);

    public async ValueTask ClickAsync(string id) => await (await Io()).InvokeVoidAsync("clickElement", id);

    public async ValueTask<bool> IsNarrowAsync() => await (await Io()).InvokeAsync<bool>("isNarrow");

    public async ValueTask<bool> FolderPickerSupportedAsync() => await (await Io()).InvokeAsync<bool>("folderPickerSupported");

    public async ValueTask DisposeAsync()
    {
        if (io is not null) await io.DisposeAsync();
        if (files is not null) await files.DisposeAsync();
    }
}
