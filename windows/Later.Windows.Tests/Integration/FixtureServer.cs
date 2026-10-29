using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Later.Windows.Tests.Integration;

/// Serves `/p/{n}` as a tiny page titled "Fixture {n}" on a free loopback port. Test project only.
internal sealed partial class FixtureServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly Task loop;
    public int Port { get; }

    [GeneratedRegex(@"^/p/(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PagePath();

    public FixtureServer()
    {
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 10) { }
        }
        loop = Task.Run(Serve);
    }

    /// The link a browser started with `--host-resolver-rules` sees: an ordinary-looking public page.
    public static string Url(int page, string query = "") => $"http://fixture.example.com/p/{page}{query}";

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try
            {
                var m = PagePath().Match(context.Request.Url!.AbsolutePath);
                if (!m.Success) { context.Response.StatusCode = 404; context.Response.Close(); continue; }
                var n = m.Groups[1].Value;
                var body = Encoding.UTF8.GetBytes($"<!doctype html><title>Fixture {n}</title><h1>Fixture {n}</h1>");
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
        }
    }

    public void Dispose()
    {
        listener.Close();
        loop.Wait(TimeSpan.FromSeconds(5));
    }
}
