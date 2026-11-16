using System.Net;
using System.Net.Sockets;
using System.Text;

namespace V380Cameras.Http;

/// <summary>
/// Minimal HTTP/1.1 server bound to the loopback interface.
///
/// The desktop build fronts the camera with Python's http.server; on Android
/// there is no second process, so this replaces both that and the C# Kestrel
/// API. Only what the bundled UI actually uses is implemented: GET/POST, query
/// strings, form bodies, and streaming bodies of unknown length.
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly Func<HttpRequest, HttpResponse, Task> handler;
    private readonly CancellationTokenSource stop = new();
    private Task? acceptLoop;

    public HttpServer(Func<HttpRequest, HttpResponse, Task> handler)
    {
        this.handler = handler;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string Origin => "http://127.0.0.1:" + Port;

    public void Start()
    {
        acceptLoop ??= Task.Run(() => AcceptAsync(stop.Token));
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient socket;
            try
            {
                socket = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(socket, ct));
        }
    }

    private async Task HandleAsync(TcpClient socket, CancellationToken ct)
    {
        using (socket)
        {
            try
            {
                socket.NoDelay = true;
                await using var stream = socket.GetStream();

                HttpRequest? request;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // A half-open connection must not keep a task alive forever.
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    request = await HttpRequest.ReadAsync(stream, readTimeout.Token);
                }

                if (request == null) return;

                var response = new HttpResponse(stream);
                try
                {
                    await handler(request, response);
                }
                catch (Exception) when (response.Started)
                {
                    // The client went away mid-stream; nothing left to say.
                }
                catch (Exception ex)
                {
                    if (!response.Started)
                        await response.SendErrorAsync(500, ex.Message);
                }

                if (!response.Started)
                    await response.SendErrorAsync(404, "Not found");
            }
            catch (Exception)
            {
                // Aborted request, client disconnect, or a malformed handshake.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { stop.Cancel(); } catch (Exception) { }
        try { listener.Stop(); } catch (Exception) { }
        if (acceptLoop != null)
        {
            try { await acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
        stop.Dispose();
    }
}

public sealed class HttpRequest
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    public string Method { get; private init; } = "GET";
    public string Target { get; private init; } = "/";
    public string Path { get; private init; } = "/";
    public Dictionary<string, string> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; private init; } = Array.Empty<byte>();

    private Dictionary<string, string>? form;

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    public string? FormValue(string name)
    {
        if (form != null) return form.TryGetValue(name, out var cached) ? cached : null;
        form = ParseForm(Body);
        return form.TryGetValue(name, out var value) ? value : null;
    }

    internal static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;

        while (buffer.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) return null;
            buffer.Write(chunk, 0, read);

            var stored = buffer.GetBuffer();
            headerEnd = IndexOfTerminator(stored, (int)buffer.Length);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        var head = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = head.Split("\r\n");
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var path = requestLine[1];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var question = path.IndexOf('?');
        if (question >= 0)
        {
            foreach (var pair in path[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var key = equals < 0 ? pair : pair[..equals];
                var value = equals < 0 ? "" : pair[(equals + 1)..];
                query[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
            }
            path = path[..question];
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int declaredLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
            int.TryParse(lengthText, out declaredLength);
        if (declaredLength < 0) declaredLength = 0;
        if (declaredLength > MaxBodyBytes) return null;

        var body = new byte[declaredLength];
        int alreadyBuffered = (int)buffer.Length - (headerEnd + 4);
        if (alreadyBuffered > 0)
            Array.Copy(buffer.GetBuffer(), headerEnd + 4, body, 0, Math.Min(alreadyBuffered, declaredLength));

        int received = Math.Max(0, Math.Min(alreadyBuffered, declaredLength));
        while (received < declaredLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(received, declaredLength - received), ct);
            if (read <= 0) break;
            received += read;
        }

        return new HttpRequest
        {
            Method = requestLine[0].ToUpperInvariant(),
            Target = requestLine[1],
            Path = Uri.UnescapeDataString(path),
            Body = body
        }.With(query, headers);
    }

    private HttpRequest With(Dictionary<string, string> query, Dictionary<string, string> headers)
    {
        foreach (var pair in query) Query[pair.Key] = pair.Value;
        foreach (var pair in headers) Headers[pair.Key] = pair.Value;
        return this;
    }

    private static Dictionary<string, string> ParseForm(byte[] body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (body.Length == 0) return result;
        var text = Encoding.UTF8.GetString(body);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static int IndexOfTerminator(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }
}

public sealed class HttpResponse
{
    private readonly NetworkStream stream;

    internal HttpResponse(NetworkStream stream) => this.stream = stream;

    public bool Started { get; private set; }

    public Stream Output => stream;

    public Task SendJsonAsync(string json, int status = 200)
        => SendAsync(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    public Task SendTextAsync(string text, int status = 200, string contentType = "text/plain; charset=utf-8")
        => SendAsync(status, contentType, Encoding.UTF8.GetBytes(text));

    public Task SendBytesAsync(byte[] body, string contentType, int status = 200, params (string Name, string Value)[] headers)
        => SendAsync(status, contentType, body, headers);

    public Task SendErrorAsync(int status, string message)
        => SendTextAsync(message, status);

    public Task SendRedirectAsync(string location)
        => SendAsync(303, "text/plain; charset=utf-8", Array.Empty<byte>(), ("Location", location));

    public async Task SendAsync(
        int status, string contentType, byte[] body,
        params (string Name, string Value)[] headers)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        foreach (var header in headers)
            head.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        Started = true;
        await WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (body.Length > 0) await WriteAsync(body);
    }

    /// <summary>Serves a file, honouring a single "Range: bytes=a-b" request.</summary>
    public async Task SendFileAsync(
        string path, string contentType, string? rangeHeader, string? disposition = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        long total = file.Length;
        long start = 0;
        long length = total;
        bool partial = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = rangeHeader["bytes=".Length..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var first = spec[..dash];
                var second = spec[(dash + 1)..];
                if (first.Length == 0)
                {
                    if (long.TryParse(second, out var suffix) && suffix > 0)
                    {
                        start = Math.Max(0, total - suffix);
                        length = total - start;
                        partial = true;
                    }
                }
                else if (long.TryParse(first, out var from))
                {
                    start = Math.Clamp(from, 0, total);
                    if (second.Length > 0 && long.TryParse(second, out var to))
                        length = Math.Clamp(to - start + 1, 0, total - start);
                    else
                        length = total - start;
                    partial = true;
                }
            }
        }

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(partial ? 206 : 200).Append(' ')
            .Append(Reason(partial ? 206 : 200)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(length).Append("\r\n");
        head.Append("Accept-Ranges: bytes\r\n");
        if (partial)
            head.Append("Content-Range: bytes ").Append(start).Append('-').Append(start + length - 1)
                .Append('/').Append(total).Append("\r\n");
        if (!string.IsNullOrEmpty(disposition))
            head.Append("Content-Disposition: ").Append(disposition).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n\r\n");

        Started = true;
        await WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));

        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = await file.ReadAsync(buffer.AsMemory(0, want));
            if (read <= 0) break;
            await WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    /// <summary>Starts a response whose length is not known up front.</summary>
    public async Task StartStreamAsync(int status, string contentType, params (string Name, string Value)[] headers)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        foreach (var header in headers)
            head.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");

        Started = true;
        await WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
    }

    public async Task WriteAsync(byte[] data)
    {
        await stream.WriteAsync(data);
        await stream.FlushAsync();
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        await stream.WriteAsync(data);
        await stream.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        303 => "See Other",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Response"
    };
}

