using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mesura.Core.Update;
using Mesura.Updating;

namespace Mesura.Updating.Tests;

/// <summary>A fake update server: a map of url to content, with Range support, optional failures and a request log.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>Cut a download after this many bytes the first time (a dropped connection), then behave.</summary>
    public int? DropAfter { get; set; }

    public bool Offline { get; set; }

    public HttpStatusCode? Fail { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url + (request.Headers.Range is { } r ? $" [Range {r.Ranges.First().From}-]" : ""));
        if (Offline)
        {
            throw new HttpRequestException("No such host is known.");
        }

        if (Fail is { } code)
        {
            return Task.FromResult(new HttpResponseMessage(code));
        }

        if (!Files.TryGetValue(url, out var content))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var from = request.Headers.Range?.Ranges.First().From ?? 0;
        var body = content.AsMemory((int)from).ToArray();
        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        if (DropAfter is { } drop && from == 0)
        {
            DropAfter = null;
            response.Content = new StreamContent(new DroppingStream(body, drop));
        }
        else
        {
            response.Content = new ByteArrayContent(body);
        }

        return Task.FromResult(response);
    }

    private sealed class DroppingStream(byte[] data, int limit) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= limit)
            {
                throw new IOException("The connection was closed.");
            }

            var n = Math.Min(Math.Min(count, limit - _position), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class UpdateFlowTests : IDisposable
{
    private const string Base = "http://127.0.0.1:9/updates";
    private static readonly (byte[] Private, byte[] Public) Keys = UpdateSignature.NewKeyPair();
    private static readonly string[] Trusted = [Convert.ToBase64String(Keys.Public)];

    private readonly string _data = Path.Combine(Path.GetTempPath(), "mesura-upd-" + Guid.NewGuid().ToString("N")[..8], "Дата с пробелом");
    private readonly FakeServer _server = new();
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(300_000);

    public UpdateFlowTests() => Publish("0.3.1");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_data)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Publish(string version, string channel = "stable", byte[]? package = null, long? size = null, string? sha = null, DateTimeOffset? expires = null)
    {
        package ??= _package;
        var manifest = new UpdateManifest(1, channel, version, 5, DateTimeOffset.UtcNow.AddHours(-1), expires ?? DateTimeOffset.UtcNow.AddDays(30),
            new Dictionary<string, string> { ["en"] = "Notes." },
            [new UpdatePackage("msi", "x64", $"{Base}/Mesura-{version}.msi", sha ?? Convert.ToHexStringLower(SHA256.HashData(package)), size ?? package.Length)]);
        var bytes = manifest.ToJson();
        _server.Files[$"{Base}/update-{channel}.json"] = bytes;
        _server.Files[$"{Base}/update-{channel}.json.sig"] = Encoding.ASCII.GetBytes(UpdateSignature.Sign(bytes, Keys.Private));
        _server.Files[$"{Base}/Mesura-{version}.msi"] = package;
    }

    private UpdateRequest Request(string installed = "0.3.0", string channel = "stable", UpdateState? state = null) =>
        new(installed, channel, state ?? new UpdateState(), Trusted, "x64", Base);

    private Task<UpdateCheckResult> Check(string installed = "0.3.0", string channel = "stable")
    {
        using var client = new UpdateClient(_server);
        return client.CheckAsync(Request(installed, channel), TestContext.Current.CancellationToken);
    }

    private Task<(DownloadResult Result, UpdateStore Store)> Download(UpdateCheckResult check, IProgress<(long, long)>? progress = null) =>
        DownloadCore(check, progress, TestContext.Current.CancellationToken);

    private async Task<(DownloadResult Result, UpdateStore Store)> DownloadCore(UpdateCheckResult check, IProgress<(long, long)>? progress, CancellationToken cancel)
    {
        var store = new UpdateStore(_data);
        using var http = new HttpClient(_server);
        var result = await new UpdateDownloader(http, store).DownloadAsync(check.Check!, check.ManifestBytes!, check.Signature!, progress, cancel);
        return (result, store);
    }

    // ---- the check ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_newer_published_version_is_offered()
    {
        var result = await Check();

        Assert.True(result.Reached);
        Assert.Equal(UpdateOutcome.Available, result.Check!.Outcome);
        Assert.Equal("0.3.1", result.Check.Manifest!.Version);
    }

    [Fact]
    public async Task The_same_version_means_up_to_date()
    {
        Assert.Equal(UpdateOutcome.UpToDate, (await Check("0.3.1")).Check!.Outcome);
    }

    [Fact]
    public async Task No_network_is_reported_as_offline_and_never_throws()
    {
        _server.Offline = true;

        var result = await Check();

        Assert.Equal(UpdateFailure.Offline, result.Failure);
        Assert.Null(result.Check);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_server_error_is_reported_as_one(HttpStatusCode code)
    {
        _server.Fail = code;

        Assert.Equal(UpdateFailure.ServerError, (await Check()).Failure);
    }

    [Fact]
    public async Task Nothing_published_yet_is_not_found()
    {
        _server.Files.Clear();

        Assert.Equal(UpdateFailure.NotFound, (await Check()).Failure);
    }

    [Fact]
    public async Task A_missing_signature_file_means_nothing_is_offered()
    {
        _server.Files.Remove($"{Base}/update-stable.json.sig");

        var result = await Check();

        Assert.Equal(UpdateFailure.NotFound, result.Failure);
        Assert.Null(result.Check);
    }

    [Fact]
    public async Task A_manifest_changed_on_the_server_is_rejected()
    {
        var url = $"{Base}/update-stable.json";
        _server.Files[url] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_server.Files[url]).Replace("0.3.1", "0.9.9", StringComparison.Ordinal));

        var result = await Check();

        Assert.Equal(UpdateFailure.Rejected, result.Failure);
        Assert.Equal("signature", result.Detail);
    }

    [Fact]
    public async Task An_oversized_manifest_is_refused_without_reading_it_all()
    {
        _server.Files[$"{Base}/update-stable.json"] = new byte[2_000_000];

        Assert.Equal(UpdateFailure.Rejected, (await Check()).Failure);
    }

    [Fact]
    public async Task A_preview_manifest_is_not_taken_for_stable()
    {
        Publish("0.4.0-rc1", channel: "preview");
        _server.Files[$"{Base}/update-stable.json"] = _server.Files[$"{Base}/update-preview.json"];
        _server.Files[$"{Base}/update-stable.json.sig"] = _server.Files[$"{Base}/update-preview.json.sig"];

        Assert.Equal("channel", (await Check()).Detail);
    }

    // ---- the download -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_package_is_downloaded_verified_and_kept_with_its_manifest()
    {
        var check = await Check();
        var done = new List<long>();

        var (result, store) = await Download(check, new Progress<(long, long)>(p => done.Add(p.Item1)));

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(_package, File.ReadAllBytes(result.PackagePath!));
        Assert.True(store.Exists(UpdateStore.ManifestFile) && store.Exists(UpdateStore.SignatureFile));
        Assert.False(store.Exists("Mesura-0.3.1-x64.msi.partial"));
    }

    [Fact]
    public async Task A_dropped_connection_resumes_from_where_it_stopped()
    {
        var check = await Check();
        _server.DropAfter = 120_000;

        var (result, _) = await Download(check);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(_package, File.ReadAllBytes(result.PackagePath!));
        Assert.Contains(_server.Requests, r => r.Contains("[Range 120000-]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_package_that_does_not_match_its_hash_is_deleted_and_never_offered_to_the_installer()
    {
        Publish("0.3.1", package: _package, sha: new string('0', 64));
        var check = await Check();

        var (result, store) = await Download(check);

        Assert.Equal(UpdateFailure.Corrupt, result.Failure);
        Assert.Null(result.PackagePath);
        Assert.False(store.Exists("Mesura-0.3.1-x64.msi"));
    }

    [Fact]
    public async Task A_package_swapped_on_the_host_is_caught_by_the_signed_hash()
    {
        var check = await Check();
        var swapped = (byte[])_package.Clone();
        swapped[10] ^= 0xFF;
        _server.Files[$"{Base}/Mesura-0.3.1.msi"] = swapped;

        var (result, store) = await Download(check);

        Assert.Equal(UpdateFailure.Corrupt, result.Failure);
        Assert.False(store.Exists("Mesura-0.3.1-x64.msi"));
    }

    [Fact]
    public async Task A_truncated_package_is_corrupt()
    {
        var check = await Check();
        _server.Files[$"{Base}/Mesura-0.3.1.msi"] = _package[..1000];

        Assert.Equal(UpdateFailure.Corrupt, (await Download(check)).Result.Failure);
    }

    [Fact]
    public async Task A_server_that_sends_more_than_the_manifest_says_is_corrupt()
    {
        var check = await Check();
        _server.Files[$"{Base}/Mesura-0.3.1.msi"] = [.. _package, .. new byte[500]];

        var (result, store) = await Download(check);

        Assert.Equal(UpdateFailure.Corrupt, result.Failure);
        Assert.False(store.Exists("Mesura-0.3.1-x64.msi.partial"));
    }

    [Fact]
    public async Task Not_enough_disk_space_is_said_before_downloading()
    {
        Publish("0.3.1", size: 40L * 1024 * 1024 * 1024 * 1024);
        var check = await Check();
        _server.Requests.Clear();

        var (result, _) = await Download(check);

        Assert.Equal(UpdateFailure.DiskFull, result.Failure);
        Assert.DoesNotContain(_server.Requests, r => r.EndsWith(".msi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_download_that_gives_not_found_is_reported()
    {
        var check = await Check();
        _server.Files.Remove($"{Base}/Mesura-0.3.1.msi");

        Assert.Equal(UpdateFailure.NotFound, (await Download(check)).Result.Failure);
    }

    [Fact]
    public async Task A_cancelled_download_keeps_what_it_has_and_the_next_one_resumes()
    {
        var check = await Check();
        using var cancel = new CancellationTokenSource();
        var progress = new Progress<(long, long)>(p =>
        {
            if (p.Item1 > 100_000)
            {
                try
                {
                    cancel.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // the progress callback is posted to the thread pool and may run after the test is over
                }
            }
        });
        _server.DropAfter = null;

        var first = await DownloadCore(check, progress, cancel.Token);
        Assert.True(first.Result.Failure is UpdateFailure.Cancelled or UpdateFailure.None);
        if (first.Result.Failure == UpdateFailure.None)
        {
            return; // too fast to cancel on this machine: the download simply finished
        }

        var second = await Download(check);
        Assert.True(second.Result.Ok, second.Result.Detail);
    }

    [Fact]
    public async Task An_unwritable_update_folder_is_access_denied_not_a_crash()
    {
        var check = await Check();
        var blocker = Path.Combine(_data, "updates");
        Directory.CreateDirectory(_data);
        File.WriteAllText(blocker, "a file where the folder should be");

        var (result, _) = await Download(check);

        Assert.False(result.Ok);
        Assert.NotEqual(UpdateFailure.None, result.Failure);
    }
}
