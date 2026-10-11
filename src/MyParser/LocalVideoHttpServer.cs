using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser;

internal sealed class LocalVideoHttpServer : IDisposable
{
    private static readonly TimeSpan RegisteredFileTtl = TimeSpan.FromHours(6);
    private const int MaxConcurrentClients = 16;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, RegisteredFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private readonly string _publicBaseUrl;

    private const string Host = "127.0.0.1";
    private const int Port = 1231;
    private const string PublicBaseUrl = "http://127.0.0.1:1231/myparser";

    public LocalVideoHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();

        _publicBaseUrl = PublicBaseUrl;

        _loopTask = Task.Run(ListenLoopAsync);
        BotLog.Info($"MyParser 本地视频 HTTP 服务已启动: listen=http://{Host}:{Port}/myparser/ via TcpListener, public_base={_publicBaseUrl}, allow_remote=false");
    }

    public string RegisterFile(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("本地视频文件不存在。", path);
        }

        CleanupExpiredRegistrations();
        var token = Guid.NewGuid().ToString("N");
        _files[token] = new RegisteredFile(path, DateTimeOffset.UtcNow);
        var fileName = Uri.EscapeDataString(Path.GetFileName(path));
        var url = $"{_publicBaseUrl}/video/{token}/{fileName}";
        BotLog.Info($"MyParser 本地视频 HTTP 已注册: token={token[..8]}, file={path}, url={url}");
        return url;
    }

    public void UnregisterFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        foreach (var item in _files)
        {
            if (string.Equals(item.Value.Path, fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                _files.TryRemove(item.Key, out _);
            }
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser 本地视频 HTTP 接收请求失败: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
        }
    }

    private void CleanupExpiredRegistrations()
    {
        var cutoff = DateTimeOffset.UtcNow - RegisteredFileTtl;
        foreach (var item in _files)
        {
            if (item.Value.RegisteredAt < cutoff || !File.Exists(item.Value.Path))
            {
                _files.TryRemove(item.Key, out _);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var clientScope = client;
        var slotAcquired = false;
        try
        {
            await _clientSlots.WaitAsync(_cts.Token);
            slotAcquired = true;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 300000;
            await using var stream = client.GetStream();
            if (!IsAllowedRemoteEndpoint(client.Client.RemoteEndPoint))
            {
                await WriteSimpleResponseAsync(stream, 403, "Forbidden", "Forbidden", _cts.Token);
                return;
            }

            var request = await ReadHttpRequestAsync(stream, _cts.Token);
            if (request is null)
            {
                return;
            }

            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed", "Method Not Allowed", _cts.Token);
                return;
            }

            var pathOnly = request.Path.Split('?', 2)[0];
            var parts = pathOnly.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !string.Equals(parts[0], "myparser", StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[1], "video", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponseAsync(stream, 404, "Not Found", "Not Found", _cts.Token);
                return;
            }

            var token = parts[2];
            if (!_files.TryGetValue(token, out var registeredFile) || !File.Exists(registeredFile.Path))
            {
                await WriteSimpleResponseAsync(stream, 404, "Not Found", "Not Found", _cts.Token);
                return;
            }

            var filePath = registeredFile.Path;
            var info = new FileInfo(filePath);
            var length = info.Length;
            var range = ParseRange(request.Headers.GetValueOrDefault("range"), length);
            var start = range.Start;
            var end = range.End;
            var count = end - start + 1;

            var statusCode = range.IsPartial ? 206 : 200;
            var statusText = range.IsPartial ? "Partial Content" : "OK";
            var headers = new List<string>
            {
                $"HTTP/1.1 {statusCode} {statusText}",
                "Server: MyParserLocalVideo",
                "Content-Type: video/mp4",
                "Accept-Ranges: bytes",
                "Cache-Control: no-store",
                $"Content-Disposition: inline; filename=\"{EscapeHeaderValue(info.Name)}\"",
                $"Content-Length: {count}",
                "Connection: close"
            };

            if (range.IsPartial)
            {
                headers.Add($"Content-Range: bytes {start}-{end}/{length}");
            }

            await WriteHeadersAsync(stream, headers, _cts.Token);
            BotLog.Info($"MyParser 本地视频 HTTP 请求: token={token[..Math.Min(8, token.Length)]}, method={request.Method}, range={(range.IsPartial ? $"{start}-{end}" : "full")}, bytes={count}, file_mb={length / 1024d / 1024d:F2}, remote={client.Client.RemoteEndPoint}");

            if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using var input = File.OpenRead(filePath);
            input.Seek(start, SeekOrigin.Begin);
            await CopyRangeAsync(input, stream, count, _cts.Token);
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            if (!IsBenignClientDisconnect(ex))
            {
                BotLog.Warning($"MyParser 本地视频 HTTP 处理请求失败: {ex.Message}");
            }
        }
        finally
        {
            if (slotAcquired)
            {
                _clientSlots.Release();
            }
        }
    }

    private static bool IsBenignClientDisconnect(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException || current is SocketException)
            {
                var message = current.Message;
                if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("connection was aborted", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("远程主机强迫关闭", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("连接被中止", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("管道", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsAllowedRemoteEndpoint(EndPoint? remoteEndPoint)
    {
        if (remoteEndPoint is not IPEndPoint ip)
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip.Address))
        {
            return true;
        }

        return false;
    }

    private static async Task<HttpRequest?> ReadHttpRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        var received = 0;
        while (received < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(received, buffer.Length - received), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            received += read;
            if (IndexOfHeaderEnd(buffer, received) >= 0)
            {
                break;
            }
        }

        var raw = Encoding.ASCII.GetString(buffer, 0, received);
        var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
        {
            return null;
        }

        var lines = raw[..headerEnd].Split("\r\n");
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new HttpRequest(requestLine[0], requestLine[1], headers);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static async Task WriteHeadersAsync(Stream stream, IEnumerable<string> headers, CancellationToken cancellationToken)
    {
        var text = string.Join("\r\n", headers) + "\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task WriteSimpleResponseAsync(Stream stream, int code, string status, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = new[]
        {
            $"HTTP/1.1 {code} {status}",
            "Server: MyParserLocalVideo",
            "Content-Type: text/plain; charset=utf-8",
            $"Content-Length: {bodyBytes.Length}",
            "Connection: close"
        };
        await WriteHeadersAsync(stream, headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private static (long Start, long End, bool IsPartial) ParseRange(string? header, long length)
    {
        if (length <= 0 || string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (0, Math.Max(0, length - 1), false);
        }

        var value = header[6..].Split(',', 2)[0].Trim();
        var dash = value.IndexOf('-');
        if (dash < 0)
        {
            return (0, length - 1, false);
        }

        var left = value[..dash].Trim();
        var right = value[(dash + 1)..].Trim();
        long start;
        long end;
        if (left.Length == 0 && long.TryParse(right, out var suffix) && suffix > 0)
        {
            start = Math.Max(0, length - suffix);
            end = length - 1;
        }
        else if (long.TryParse(left, out start))
        {
            end = long.TryParse(right, out var parsedEnd) ? parsedEnd : length - 1;
            start = Math.Clamp(start, 0, length - 1);
            end = Math.Clamp(end, start, length - 1);
        }
        else
        {
            return (0, length - 1, false);
        }

        return (start, end, true);
    }

    private static async Task CopyRangeAsync(Stream input, Stream output, long bytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long remaining = bytes;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static string EscapeHeaderValue(string value) => value.Replace("\\", "_").Replace("\"", "_").Replace("\r", "_").Replace("\n", "_");

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore shutdown errors
        }

        try
        {
            _loopTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore shutdown errors
        }

        _clientSlots.Dispose();
        _cts.Dispose();
    }

    private sealed record RegisteredFile(string Path, DateTimeOffset RegisteredAt);

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers);
}
