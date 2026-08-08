using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hackermes.PacketTraffic.Tests;

public sealed class LocalHttpServerFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalHttpServerFixture()
    {
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _server = ServeOnceAsync();
    }

    public Uri BaseUri { get; }
    public Task<string> Request => _request.Task;

    private async Task ServeOnceAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var builder = new StringBuilder();
            var contentLength = 0;
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                builder.Append(line).Append("\r\n");
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line[15..].Trim(), out contentLength);
                if (line.Length == 0) break;
            }
            var body = new char[contentLength];
            var read = 0;
            while (read < body.Length)
                read += await reader.ReadAsync(body.AsMemory(read), _stop.Token);
            builder.Append(body);
            _request.TrySetResult(builder.ToString());

            const string responseBody = "{\"accepted\":true}";
            var response = "HTTP/1.1 202 Accepted\r\nContent-Type: application/json\r\nX-Test: local-fixture\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\nConnection: close\r\n\r\n{responseBody}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _request.TrySetException(error); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _server; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
