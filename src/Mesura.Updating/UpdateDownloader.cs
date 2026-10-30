using System.Net;
using System.Net.Http.Headers;
using Mesura.Core.Update;

namespace Mesura.Updating;

public sealed record DownloadResult(UpdateFailure Failure, string Detail, string? PackagePath)
{
    public bool Ok => Failure == UpdateFailure.None;
}

/// <summary>
/// Fetches the package a check found, into the update store, and proves it is the package the signed manifest describes
/// before anyone may run it: the size, then the SHA-256 against the manifest, which was verified when it was judged. The file is
/// written as <c>.partial</c> and renamed only when whole; a broken connection resumes where it stopped (HTTP Range), and a
/// package that fails its hash is deleted, never kept. Needs room for the package twice over plus the installer's own working
/// space; says so before it downloads rather than after.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, UpdateStore store)
{
    public const long SpareBytes = 150L * 1024 * 1024;
    private const int Attempts = 3;

    public static string PackageName(UpdateCheck check) => $"Mesura-{check.Manifest!.Version}-{check.Package!.Architecture}.msi";

    public async Task<DownloadResult> DownloadAsync(UpdateCheck check, byte[] manifestBytes, string signature, IProgress<(long Done, long Total)>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check is not { Package: { } package, Manifest: not null } || check.Outcome is UpdateOutcome.UpToDate or UpdateOutcome.Rejected)
        {
            return new DownloadResult(UpdateFailure.Rejected, "nothing to download", null);
        }

        var final = PackageName(check);
        var partial = final + ".partial";
        try
        {
            store.Ensure();
            var needed = package.Size * 2 + SpareBytes;
            if (store.FreeBytes() is { } free && free < needed)
            {
                return new DownloadResult(UpdateFailure.DiskFull, $"{free} bytes free, {needed} needed", null);
            }

            // The manifest and its signature travel with the package: the updater verifies them again before it runs anything.
            store.Write(UpdateStore.ManifestFile, manifestBytes);
            store.WriteText(UpdateStore.SignatureFile, signature);

            if (store.Exists(final) && await IsWholeAsync(final, package, cancel))
            {
                return new DownloadResult(UpdateFailure.None, "", store.PathOf(final));
            }

            store.Delete(final);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await FetchAsync(package, partial, progress, cancel);
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or IOException && !IsDiskFull(e) && attempt < Attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancel); // the partial file stays: the next attempt resumes it
                }
            }

            if (store.Length(partial) != package.Size)
            {
                var got = store.Length(partial);
                store.Delete(partial);
                return new DownloadResult(UpdateFailure.Corrupt, $"{got} of {package.Size} bytes", null);
            }

            store.Commit(partial, final);
            if (!await IsWholeAsync(final, package, cancel))
            {
                store.Delete(final);
                return new DownloadResult(UpdateFailure.Corrupt, "the SHA-256 differs from the signed manifest", null);
            }

            return new DownloadResult(UpdateFailure.None, "", store.PathOf(final));
        }
        catch (OperationCanceledException)
        {
            return new DownloadResult(UpdateFailure.Cancelled, "cancelled", null); // the partial file stays, so the next try resumes
        }
        catch (UnauthorizedAccessException e)
        {
            return new DownloadResult(UpdateFailure.AccessDenied, e.Message, null);
        }
        catch (HttpRequestException e)
        {
            var failure = e.StatusCode is null ? UpdateFailure.Offline : e.StatusCode == HttpStatusCode.NotFound ? UpdateFailure.NotFound : UpdateFailure.ServerError;
            return new DownloadResult(failure, e.Message, null);
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            store.Delete(partial);
            return new DownloadResult(UpdateFailure.DiskFull, e.Message, null);
        }
        catch (IOException e)
        {
            return new DownloadResult(UpdateFailure.Other, e.Message, null);
        }
        catch (InvalidDataException e)
        {
            store.Delete(partial);
            return new DownloadResult(UpdateFailure.Corrupt, e.Message, null);
        }
    }

    private async Task FetchAsync(UpdatePackage package, string partial, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        var have = store.Length(partial);
        if (have >= package.Size)
        {
            store.Delete(partial); // already as long as it should be (or longer): no use to resume, fetch it whole
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            store.Delete(partial);
            using var again = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            again.EnsureSuccessStatusCode();
            await CopyAsync(again, partial, append: false, 0, package.Size, progress, cancel);
            return;
        }

        response.EnsureSuccessStatusCode();
        var resumed = response.StatusCode == HttpStatusCode.PartialContent && have > 0;
        await CopyAsync(response, partial, resumed, resumed ? have : 0, package.Size, progress, cancel);
    }

    private async Task CopyAsync(HttpResponseMessage response, string partial, bool append, long already, long total, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = store.OpenPartial(partial, append);
        var buffer = new byte[128 * 1024];
        var done = already;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await input.ReadAsync(buffer, cancel)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            if (done > total)
            {
                throw new InvalidDataException("The server sent more than the manifest says.");
            }

            if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(100))
            {
                progress.Report((done, total));
                lastReport = DateTime.UtcNow;
            }
        }

        progress?.Report((done, total));
    }

    private async Task<bool> IsWholeAsync(string name, UpdatePackage package, CancellationToken cancel)
    {
        if (store.Length(name) != package.Size)
        {
            return false;
        }

        await using var stream = store.OpenRead(name);
        var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancel));
        return string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiskFull(Exception e) => e is IOException io && (io.HResult & 0xFFFF) is 112 or 39; // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
}
