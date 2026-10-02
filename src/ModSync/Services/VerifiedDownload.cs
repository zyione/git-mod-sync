using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using ModSync.Models;
using ModSync.Utils;

namespace ModSync.Services;

public sealed class VerifiedDownload
{
    private readonly HttpClient _http;
    public VerifiedDownload(HttpClient http) => _http = http;

    public static void ValidateAsset(UpdateAsset asset)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            asset.Size <= 0 || asset.Size > BinaryDelta.MaxOutputBytes || asset.Sha256.Length != 64 ||
            !asset.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release has no valid, verifiable update asset.");
    }

    public async Task DownloadAsync(UpdateAsset asset, string path, Action<SyncProgressInfo>? progress = null,
        CancellationToken cancellation = default)
    {
        ValidateAsset(asset);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && new FileInfo(path).Length == asset.Size && BinaryDelta.Hash(path) == asset.Sha256.ToLowerInvariant()) return;
        string partial = path + ".part";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset >= asset.Size) { File.Delete(partial); offset = 0; }
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
                request.Headers.Add("User-Agent", "ModSync-Updater");
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(partial);
                    throw new IOException("The server could not resume this download; retrying from the start.");
                }
                response.EnsureSuccessStatusCode();
                bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                if (resumed && (response.Content.Headers.ContentRange?.From != offset ||
                    response.Content.Headers.ContentRange?.Length != asset.Size))
                    throw new InvalidDataException("The server returned an unexpected download range.");
                if (!resumed) offset = 0;
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long total = offset;
                    var timer = Stopwatch.StartNew();
                    long lastReport = -100;
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        int read;
                        try { read = await input.ReadAsync(buffer, idle.Token); }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                        { throw new IOException("The update download stalled. Retrying..."); }
                        if (read == 0) break;
                        if (total + read > asset.Size) throw new InvalidDataException("Update download exceeded the declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        total += read;
                        if (timer.ElapsedMilliseconds - lastReport < 100 && total != asset.Size) continue;
                        lastReport = timer.ElapsedMilliseconds;
                        double speed = (total - offset) / Math.Max(0.1, timer.Elapsed.TotalSeconds);
                        progress?.Invoke(SyncProgressInfo.Determinate("Downloading update...", total * 100.0 / asset.Size,
                            $"{PathUtils.FormatFileSize(total)} of {PathUtils.FormatFileSize(asset.Size)}",
                            $"{PathUtils.FormatSpeed(speed)} • {PathUtils.FormatEta((asset.Size - total) / Math.Max(1, speed))}"));
                    }
                }
                if (new FileInfo(partial).Length != asset.Size) throw new IOException("Update download is incomplete.");
                if (!BinaryDelta.Hash(partial).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update checksum verification failed.");
                File.Move(partial, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException ||
                ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                if (ex is InvalidDataException && File.Exists(partial)) File.Delete(partial);
                if (attempt == 2) throw;
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellation);
            }
        }
    }
}
