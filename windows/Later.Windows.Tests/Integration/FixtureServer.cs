using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Later.Windows.Tests.Integration;

/// Serves `/p/{n}` as a tiny page titled "Fixture {n}" and `/heavy/{n}` as a large one titled "Heavy {n}", on a free loopback port. Test project only.
internal sealed partial class FixtureServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly Task loop;
    public int Port { get; }

    [GeneratedRegex(@"^/(p|heavy)/(\d+)$", RegexOptions.CultureInvariant)]
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

    public static string HeavyUrl(int page) => $"http://fixture.example.com/heavy/{page}";

    /// About 5,600 elements: 1,200 paragraphs with a link and a 800-row, 3-column table, plus a script that keeps changing text.
    static string HeavyBody(string n)
    {
        var sb = new StringBuilder($"<!doctype html><title>Heavy {n}</title><h1>Heavy {n}</h1><div id=\"live\">0</div>");
        for (int i = 0; i < 1200; i++) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<p>Paragraph {i} of page {n} <a href=\"/heavy/{n}#{i}\">link {i}</a></p>");
        sb.Append("<table>");
        for (int r = 0; r < 800; r++) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<tr><td>{r}</td><td>row {r}</td><td>value {r * 7}</td></tr>");
        sb.Append("</table><script>let k=0;setInterval(()=>{document.getElementById('live').textContent=++k},200)</script>");
        return sb.ToString();
    }

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
                var n = m.Groups[2].Value;
                var body = Encoding.UTF8.GetBytes(m.Groups[1].Value == "heavy" ? HeavyBody(n) : $"<!doctype html><title>Fixture {n}</title><h1>Fixture {n}</h1>");
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
