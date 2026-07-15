using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TempMonitor.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v6.1.0", 6, 1, 0)]
    [InlineData("6.2.3", 6, 2, 3)]
    [InlineData("V7.0.0-beta.1", 7, 0, 0)]
    [InlineData("v8.0.1+build.42", 8, 0, 1)]
    public void TryParseVersion_AcceptsReleaseTags(
        string tag,
        int major,
        int minor,
        int build)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out Version? version));
        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void TryParseVersion_RejectsInvalidTags(string? tag) =>
        Assert.False(UpdateChecker.TryParseVersion(tag, out _));

    [Fact]
    public async Task ParseResponseAsync_ParsesValidReleaseAndComparesVersions()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"tag_name\":\"v6.2.0\"}"));

        UpdateCheckResult result = await UpdateChecker.ParseResponseAsync(
            stream,
            stream.Length,
            new Version(6, 1, 0));

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(6, 2, 0), result.LatestVersion);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"tag_name\":42}")]
    [InlineData("{\"other\":\"v6.2.0\"}")]
    public async Task ParseResponseAsync_RejectsMalformedReleaseResponses(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.ParseResponseAsync(
            stream,
            stream.Length,
            new Version(6, 1, 0)));
    }

    [Fact]
    public async Task ParseResponseAsync_RejectsOversizedResponsesWithOrWithoutLengthHeader()
    {
        await using var declaredTooLarge = new MemoryStream([]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.ParseResponseAsync(
            declaredTooLarge,
            65_537,
            new Version(6, 1, 0)));

        string largeJson = "{\"tag_name\":\"v" + new string('1', 70_000) + "\"}";
        await using var streamedTooLarge = new MemoryStream(Encoding.UTF8.GetBytes(largeJson));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.ParseResponseAsync(
            streamedTooLarge,
            null,
            new Version(6, 1, 0)));
    }

    [Fact]
    public async Task CheckAsync_TimesOutWhileReadingResponseBody()
    {
        using var client = new HttpClient(new StallingResponseHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        await Assert.ThrowsAsync<TaskCanceledException>(() => UpdateChecker.CheckAsync(
            client,
            new Version(6, 1, 0),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None));
    }

    private sealed class StallingResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            });
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
