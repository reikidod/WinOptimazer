using System.Net;
using System.Text;

namespace WinOptimizer.Services;

/// <summary>
/// Serves the bundled Website/ folder on a loopback HTTP endpoint so the
/// landing page opens in the browser without an external host.
/// </summary>
public sealed class LocalSiteHost : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string? _root;
    private int _port;

    public string? BaseUrl { get; private set; }

    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>Starts the host if needed and returns the URL of index.html.</summary>
    public string EnsureStarted()
    {
        if (IsRunning && BaseUrl is not null)
            return BaseUrl;

        _root = ResolveWebsiteRoot();
        if (_root is null)
            throw new DirectoryNotFoundException(
                "Папка Website не найдена рядом с приложением. Переустановите WinOptimizer.");

        Exception? last = null;
        foreach (var port in new[] { 47831, 47832, 47833, 0 })
        {
            try
            {
                StartOnPort(port == 0 ? PickFreePort() : port);
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                StopInternal();
            }
        }

        if (last is not null || BaseUrl is null)
            throw last ?? new InvalidOperationException("Не удалось запустить локальный сайт.");

        return BaseUrl;
    }

    public void Dispose() => StopInternal();

    private void StartOnPort(int port)
    {
        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        _listener = listener;
        _port = port;
        BaseUrl = prefix;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            if (path is "/" or "")
                path = "/index.html";

            var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_root!, relative));
            if (!full.StartsWith(_root!, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes("Not found");
                ctx.Response.OutputStream.Write(msg);
                ctx.Response.Close();
                return;
            }

            var bytes = File.ReadAllBytes(full);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = Mime(full);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    private static string? ResolveWebsiteRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Website"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Website")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Website")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Website")),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "index.html")))
                return Path.GetFullPath(c);
        }
        return null;
    }

    private static int PickFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private void StopInternal()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        _cts = null;
        _loop = null;
        BaseUrl = null;
    }
}
