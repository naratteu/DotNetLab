using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text.Json;
using Handler = System.Func<string, string, string[], byte[], System.Threading.Tasks.Task<(int, string[], byte[])>>;
using SocketOpener = System.Func<string, string[], string[], System.Action<bool, byte[]>, System.Action<int, string>,
    System.Threading.Tasks.Task<(string?, System.Func<bool, byte[], System.Threading.Tasks.Task>, System.Func<int, string, System.Threading.Tasks.Task>)>>;

namespace DotNetLab;

/// <summary>
/// Keeps the server of the last executed web program (<c>#:sdk Microsoft.NET.Sdk.Web</c>) running
/// and exposes it publicly through a Portal relay (<c>wwwroot/portal/bridge.js</c>).
/// The public address stays the same across runs, only the program behind it changes.
/// </summary>
public static partial class WebServer
{
    // Shared with the hosting shim compiled into the program, which can only use core library types.
    public const string StartedKey = "DotNetLab.WebServer.Started";
    public const string StopKey = "DotNetLab.WebServer.Stop";
    public const string RunningKey = "DotNetLab.WebServer.Running";
    public const string AssetsKey = "DotNetLab.WebServer.Assets";
    public const string HandlerType = "Func<string, string, string[], byte[], Task<(int, string[], byte[])>>";
    public const string SocketOpenerType = "Func<string, string[], string[], Action<bool, byte[]>, Action<int, string>, Task<(string?, Func<bool, byte[], Task>, Func<int, string, Task>)>>";
    public const string ShimTypeName = "DotNetLab.Generated.WebHostingShim";
    public const string RelayMetadataKey = "DotNetLab.PortalRelay";

    private const string ModuleName = "dotnetlab-portal";

    private static Handler? handler;
    private static SocketOpener? socketOpener;
    private static Func<Task>? stop;
    private static AssemblyLoadContext? loadContext;
    private static Task? moduleImport;
    private static readonly Dictionary<int, (Func<bool, byte[], Task> Send, Func<int, string, Task> Close)> sockets = [];

    /// <summary>
    /// Static files of the ASP.NET Core version which runs the programs (e.g., <c>_framework/blazor.web.js</c>)
    /// which are normally served from the build output.
    /// </summary>
    public static ImmutableDictionary<string, ImmutableArray<byte>> FrameworkAssets { get; set; } = ImmutableDictionary<string, ImmutableArray<byte>>.Empty;

    public static bool IsRunning => handler != null;

    /// <summary>
    /// Whether the code has <c>#:sdk Microsoft.NET.Sdk.Web</c>. Outputs of such programs (like the public URL)
    /// are valid only while their run lasts, so they must not be cached or shared.
    /// </summary>
    public static bool IsWebProgram(IEnumerable<string> texts)
    {
        return texts.Any(static text => WebSdkDirective().IsMatch(text));
    }

    [GeneratedRegex(@"^\s*#:sdk\s+Microsoft\.NET\.Sdk\.Web\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex WebSdkDirective();

    /// <summary>
    /// Stops the previous server so that the next run starts from a clean slate.
    /// </summary>
    public static async Task StopAsync()
    {
        handler = null;
        socketOpener = null;
        AppContext.SetData(StartedKey, null);
        AppContext.SetData(StopKey, null);
        AppContext.SetData(RunningKey, null);

        if (OperatingSystem.IsBrowser())
        {
            foreach (var id in sockets.Keys)
            {
                Interop.SocketClosed(id, 1012, "Server restarted");
            }
        }
        sockets.Clear();

        if (Interlocked.Exchange(ref stop, null) is { } stopServer)
        {
            try
            {
                await stopServer();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Stopping the previous server failed: {ex}");
            }
        }

        Interlocked.Exchange(ref loadContext, null)?.Unload();
    }

    /// <summary>
    /// Called before running a web program so its hosting shim can serve <see cref="FrameworkAssets"/>.
    /// </summary>
    internal static void Prepare()
    {
        var assets = FrameworkAssets;
        AppContext.SetData(AssetsKey, new Func<string, byte[]?>(path =>
            assets.TryGetValue(path, out var bytes) ? ImmutableCollectionsMarshal.AsArray(bytes) : null));
    }

    internal static void Adopt(Handler handler, SocketOpener socketOpener, AssemblyLoadContext loadContext)
    {
        WebServer.stop = AppContext.GetData(StopKey) as Func<Task>;
        WebServer.loadContext = loadContext;
        WebServer.socketOpener = socketOpener;
        WebServer.handler = handler;
    }

    /// <returns>
    /// The public URL of the server, or an explanation why there is none.
    /// </returns>
    internal static async Task<string> ExposeAsync(string? relay)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return "(Exposing servers is only supported in the browser.)";
        }

        try
        {
            await (moduleImport ??= JSHost.ImportAsync(ModuleName, "../portal/bridge.js"));
            return await Interop.ExposeAsync(relay ?? "", OnRequest);
        }
        catch (Exception ex)
        {
            moduleImport = null;
            return $"(Could not expose the server: {ex.Message})";
        }
    }

    [SupportedOSPlatform("browser")]
    private static void OnRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int id = root.GetProperty("id").GetInt32();

        switch (root.GetProperty("kind").GetString())
        {
            case "http":
                HandleAsync(id,
                    root.GetProperty("method").GetString()!,
                    root.GetProperty("url").GetString()!,
                    GetHeaders(root),
                    root.GetProperty("body").GetBytesFromBase64());
                break;
            case "websocket":
                OpenSocketAsync(id,
                    root.GetProperty("url").GetString()!,
                    GetHeaders(root),
                    root.GetProperty("protocols").EnumerateArray().Select(static p => p.GetString() ?? "").ToArray());
                break;
            case "send":
                if (sockets.TryGetValue(id, out var socket))
                {
                    _ = socket.Send(root.GetProperty("text").GetBoolean(), root.GetProperty("body").GetBytesFromBase64());
                }
                break;
            case "close":
                if (sockets.Remove(id, out socket))
                {
                    _ = socket.Close(root.GetProperty("code").GetInt32(), root.GetProperty("reason").GetString() ?? "");
                }
                break;
        }

        static string[] GetHeaders(JsonElement root)
        {
            return root.GetProperty("headers").EnumerateArray().Select(static h => h.GetString() ?? "").ToArray();
        }
    }

    [SupportedOSPlatform("browser")]
    private static async void HandleAsync(int id, string method, string url, string[] headers, byte[] body)
    {
        try
        {
            if (handler is not { } current)
            {
                Interop.Respond(id, 503, """["Content-Type","text/plain; charset=utf-8"]""",
                    Encoding.UTF8.GetBytes("No server is running. Run a program with '#:sdk Microsoft.NET.Sdk.Web' in DotNetLab."));
                return;
            }

            var (status, responseHeaders, responseBody) = await current(method, url, headers, body);

            Interop.Respond(id, status, JsonSerializer.Serialize(responseHeaders, WebServerJsonContext.Default.StringArray), responseBody);
        }
        catch (Exception ex)
        {
            Interop.Respond(id, 500, """["Content-Type","text/plain; charset=utf-8"]""", Encoding.UTF8.GetBytes(ex.ToString()));
        }
    }

    [SupportedOSPlatform("browser")]
    private static async void OpenSocketAsync(int id, string url, string[] headers, string[] protocols)
    {
        try
        {
            if (socketOpener is not { } current)
            {
                Interop.SocketFailed(id, "No server is running.");
                return;
            }

            var (protocol, send, close) = await current(url, headers, protocols,
                (text, data) => Interop.SocketMessage(id, text, data),
                (code, reason) =>
                {
                    if (sockets.Remove(id))
                    {
                        Interop.SocketClosed(id, code, reason);
                    }
                });

            sockets[id] = (send, close);
            Interop.SocketOpened(id, protocol ?? "");
        }
        catch (Exception ex)
        {
            Interop.SocketFailed(id, ex.Message);
        }
    }

    [SupportedOSPlatform("browser")]
    private static partial class Interop
    {
        [JSImport("expose", ModuleName)]
        public static partial Task<string> ExposeAsync(
            string relay,
            [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> onRequest);

        [JSImport("respond", ModuleName)]
        public static partial void Respond(int id, int status, string headersJson, byte[] body);

        [JSImport("socketOpened", ModuleName)]
        public static partial void SocketOpened(int id, string protocol);

        [JSImport("socketFailed", ModuleName)]
        public static partial void SocketFailed(int id, string message);

        [JSImport("socketMessage", ModuleName)]
        public static partial void SocketMessage(int id, bool text, byte[] data);

        [JSImport("socketClosed", ModuleName)]
        public static partial void SocketClosed(int id, int code, string reason);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class WebServerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
