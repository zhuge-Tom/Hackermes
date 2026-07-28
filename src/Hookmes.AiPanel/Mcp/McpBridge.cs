using Hookmes.AiPanel.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hookmes.AiPanel.Mcp;

public sealed record McpServerDescriptor(string Id, string DisplayName, string Transport, string Endpoint);
public sealed record McpToolDescriptor(string ServerId, string Name, string Description, JsonElement InputSchema);
public sealed record McpStdioServer(string Id, string Command, IReadOnlyList<string> Arguments);

public interface IMcpBridge : IAsyncDisposable
{
    IReadOnlyList<McpServerDescriptor> Servers { get; }
    Task ConnectAsync(McpStdioServer server, CancellationToken ct = default);
    IAsyncEnumerable<McpToolDescriptor> EnumerateToolsAsync(CancellationToken ct = default);
    ValueTask<ToolResult> InvokeAsync(string serverId, ToolInvocation invocation, CancellationToken ct = default);
}

/// <summary>可工作的 MCP stdio JSON-RPC 客户端，支持 initialize、tools/list 与 tools/call。</summary>
public sealed class StdioMcpBridge : IMcpBridge
{
    private readonly ConcurrentDictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);

    public IReadOnlyList<McpServerDescriptor> Servers => [.. _connections.Values.Select(connection => connection.Descriptor)];

    public async Task ConnectAsync(McpStdioServer server, CancellationToken ct = default)
    {
        if (_connections.ContainsKey(server.Id)) return;
        var connection = await McpConnection.StartAsync(server, ct).ConfigureAwait(false);
        if (!_connections.TryAdd(server.Id, connection)) await connection.DisposeAsync().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<McpToolDescriptor> EnumerateToolsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (serverId, connection) in _connections)
        {
            using var response = await connection.RequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
            if (!response.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("tools", out var tools)) continue;
            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var description = tool.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty;
                var schema = tool.TryGetProperty("inputSchema", out var input)
                    ? input.Clone() : JsonSerializer.SerializeToElement(new { type = "object" });
                yield return new McpToolDescriptor(serverId, name, description, schema);
            }
        }
    }

    public async ValueTask<ToolResult> InvokeAsync(
        string serverId, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(serverId, out var connection))
            return ToolResult.Fail($"MCP server '{serverId}' is not connected.");
        using var response = await connection.RequestAsync("tools/call", new
        {
            name = invocation.ToolName,
            arguments = invocation.Arguments
        }, ct).ConfigureAwait(false);
        if (response.RootElement.TryGetProperty("error", out var error)) return ToolResult.Fail(error.ToString());
        var result = response.RootElement.GetProperty("result");
        var failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
        return failed ? ToolResult.Fail(result.ToString()) : ToolResult.Ok(result.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();
    }

    private sealed class McpConnection : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Task _readLoop;
        private readonly Task _errorLoop;
        private long _nextId;

        private McpConnection(Process process, string id)
        {
            _process = process;
            _input = process.StandardInput;
            Descriptor = new McpServerDescriptor(id, id, "stdio", process.StartInfo.FileName);
            _readLoop = ReadLoopAsync(process.StandardOutput, _lifetime.Token);
            _errorLoop = DrainErrorsAsync(process.StandardError, _lifetime.Token);
        }

        public McpServerDescriptor Descriptor { get; }

        public static async Task<McpConnection> StartAsync(McpStdioServer server, CancellationToken ct)
        {
            var start = new ProcessStartInfo
            {
                FileName = server.Command, UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in server.Arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 MCP server: {server.Id}");
            var connection = new McpConnection(process, server.Id);
            try
            {
                using var initialized = await connection.RequestAsync("initialize", new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "Hookmes", version = "0.1.0" }
                }, ct).ConfigureAwait(false);
                await connection.NotifyAsync("notifications/initialized", new { }, ct).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            try
            {
                await WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), ct)
                    .ConfigureAwait(false);
                return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        private Task NotifyAsync(string method, object parameters, CancellationToken ct) =>
            WriteAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }), ct);

        private async Task WriteAsync(string json, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try { await _input.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false); await _input.FlushAsync(ct).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        }

        private async Task ReadLoopAsync(StreamReader output, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    JsonDocument document;
                    try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                    if (document.RootElement.TryGetProperty("id", out var idElement)
                        && idElement.TryGetInt64(out var id) && _pending.TryGetValue(id, out var completion))
                        completion.TrySetResult(document);
                    else document.Dispose();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (var completion in _pending.Values)
                    completion.TrySetException(new IOException($"MCP server '{Descriptor.Id}' 已断开"));
            }
        }

        private static async Task DrainErrorsAsync(StreamReader errors, CancellationToken ct)
        {
            try { while (!ct.IsCancellationRequested && await errors.ReadLineAsync(ct).ConfigureAwait(false) is not null) { } }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try { _input.Close(); if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(_readLoop, _errorLoop).ConfigureAwait(false); } catch { }
            _process.Dispose(); _writeGate.Dispose(); _lifetime.Dispose();
        }
    }
}
