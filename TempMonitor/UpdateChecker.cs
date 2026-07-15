using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TempMonitor;

internal readonly record struct UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string LatestTag)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

internal static class UpdateChecker
{
    public const string ReleasesUrl = "https://github.com/gxmst/gxTempMonitor/releases/latest";
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/gxmst/gxTempMonitor/releases/latest";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Client = CreateClient();

    public static Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Version currentVersion = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);
        return CheckAsync(Client, currentVersion, RequestTimeout, cancellationToken);
    }

    internal static async Task<UpdateCheckResult> CheckAsync(
        HttpClient client,
        Version currentVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(timeout);
        CancellationToken requestToken = requestCancellation.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            return await ParseResponseAsync(
                stream,
                response.Content.Headers.ContentLength,
                currentVersion,
                requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && requestCancellation.IsCancellationRequested)
        {
            throw new TaskCanceledException("The update check timed out.", exception);
        }
    }

    internal static async Task<UpdateCheckResult> ParseResponseAsync(
        Stream stream,
        long? contentLength,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (contentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The update response was unexpectedly large.");

        try
        {
            using var limitedStream = new LengthLimitedReadStream(stream, MaximumResponseBytes);
            using JsonDocument document = await JsonDocument.ParseAsync(
                limitedStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) ||
                tagElement.ValueKind != JsonValueKind.String ||
                tagElement.GetString() is not string tag ||
                !TryParseVersion(tag, out Version? latestVersion))
            {
                throw new InvalidDataException("The latest release did not contain a valid version tag.");
            }

            return new UpdateCheckResult(currentVersion, latestVersion!, tag);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The update response was not valid JSON.", exception);
        }
    }

    internal static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        int suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            normalized = normalized[..suffix];
        return Version.TryParse(normalized, out version);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        string productVersion = typeof(UpdateChecker).Assembly.GetName().Version?.ToString(2) ?? "0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("gxTempMonitor", productVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class LengthLimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumLength;
        private long _totalRead;

        public LengthLimitedReadStream(Stream inner, long maximumLength)
        {
            _inner = inner;
            _maximumLength = maximumLength;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _totalRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int allowed = (int)Math.Min(buffer.Length, _maximumLength - _totalRead);
            if (allowed <= 0)
            {
                byte[] probe = new byte[1];
                int extra = await _inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
                if (extra == 0) return 0;
                throw new InvalidDataException("The update response exceeded the size limit.");
            }

            int read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
            _totalRead += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
