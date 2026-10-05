using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text.Json;

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
    public const string HandlerType = "Func<string, string, string[], byte[], Task<(int, string[], byte[])>>";
    public const string ShimTypeName = "DotNetLab.Generated.WebHostingShim";
    public const string RelayMetadataKey = "DotNetLab.PortalRelay";

    private const string ModuleName = "dotnetlab-portal";

    private static Func<string, string, string[], byte[], Task<(int, string[], byte[])>>? handler;
    private static Func<Task>? stop;
    private static AssemblyLoadContext? loadContext;
    private static Task? moduleImport;

    public static bool IsRunning => handler != null;

    /// <summary>
    /// Stops the previous server so that the next run starts from a clean slate.
    /// </summary>
    public static async Task StopAsync()
    {
        handler = null;
        AppContext.SetData(StartedKey, null);
        AppContext.SetData(StopKey, null);
        AppContext.SetData(RunningKey, null);

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

    internal static void Adopt(
        Func<string, string, string[], byte[], Task<(int, string[], byte[])>> handler,
        AssemblyLoadContext loadContext)
    {
        WebServer.stop = AppContext.GetData(StopKey) as Func<Task>;
        WebServer.loadContext = loadContext;
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
    private static async void OnRequest(string json)
    {
        int id = -1;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            id = root.GetProperty("id").GetInt32();

            if (handler is not { } current)
            {
                Interop.Respond(id, 503, """["Content-Type","text/plain; charset=utf-8"]""",
                    Encoding.UTF8.GetBytes("No server is running. Run a program with '#:sdk Microsoft.NET.Sdk.Web' in DotNetLab."));
                return;
            }

            var headers = root.GetProperty("headers").EnumerateArray().Select(static h => h.GetString() ?? "").ToArray();
            var body = root.GetProperty("body").GetBytesFromBase64();

            var (status, responseHeaders, responseBody) = await current(
                root.GetProperty("method").GetString()!,
                root.GetProperty("url").GetString()!,
                headers,
                body);

            Interop.Respond(id, status, JsonSerializer.Serialize(responseHeaders, WebServerJsonContext.Default.StringArray), responseBody);
        }
        catch (Exception ex)
        {
            if (id >= 0)
            {
                Interop.Respond(id, 500, """["Content-Type","text/plain; charset=utf-8"]""", Encoding.UTF8.GetBytes(ex.ToString()));
            }
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
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class WebServerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
