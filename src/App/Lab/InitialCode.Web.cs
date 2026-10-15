namespace DotNetLab.Lab;

// Templates of web servers. They run in the browser tab and get a public address through a Portal relay
// (see `WebServer` in DotNetLab.Shared). Workarounds specific to a library live in a separate file of the
// template, so that they can be seen and changed.
internal sealed partial record InitialCode
{
    public static readonly InitialCode MinimalApi = new("Program.cs", """
        #:sdk Microsoft.NET.Sdk.Web
        // This server runs in this browser tab and gets a public address through a Portal relay (see the Run output).
        // DotNetLab swaps Kestrel for an in-memory server behind the scenes, so this is regular ASP.NET Core code.
        // 💡 TIP: Choose the relay with `#:property PortalRelay=https://...`.

        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        var visits = 0;

        app.MapGet("/", () => "Hello from ASP.NET Core running in a browser tab!");
        app.MapGet("/hello/{name}", (string name) => new { Greeting = $"Hello, {name}!", Visit = Interlocked.Increment(ref visits) });
        app.MapPost("/echo", async (HttpRequest request) => await new StreamReader(request.Body).ReadToEndAsync());

        app.Run();

        """.ReplaceLineEndings());

    public static readonly InitialCode BlazorProgram = new("Program.cs", """
        #:sdk Microsoft.NET.Sdk.Web
        // A Blazor Server app running in this browser tab, with a public address through a Portal relay (see the Run output).
        // DotNetLab swaps Kestrel for an in-memory server behind the scenes, so this is regular ASP.NET Core code.
        // 💡 TIP: Choose the relay with `#:property PortalRelay=https://...`.

        using TestNamespace; // Razor components are generated in this namespace.

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Run();

        """.ReplaceLineEndings());

    public static readonly InitialCode BlazorApp = new("App.razor", """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <base href="/" />
            <ResourcePreloader />
            <ImportMap />
            <HeadOutlet @rendermode="InteractiveServer" />
        </head>
        <body>
            <Routes @rendermode="InteractiveServer" />
            <script src="@Assets["_framework/blazor.web.js"]"></script>
        </body>
        </html>

        """.ReplaceLineEndings());

    public static readonly InitialCode BlazorRoutes = new("Routes.razor", """
        <Router AppAssembly="typeof(Program).Assembly">
            <Found Context="routeData">
                <RouteView RouteData="routeData" />
            </Found>
        </Router>

        """.ReplaceLineEndings());

    public static readonly InitialCode BlazorCounter = new("Counter.razor", """
        @page "/"

        <PageTitle>Counter</PageTitle>

        <h1>Counter</h1>

        <p role="status">Current count: @currentCount</p>

        <button @onclick="IncrementCount">Click me</button>

        <p>Each visitor has their own count: it lives in their circuit, on the server in this browser tab.</p>

        @code {
            private int currentCount = 0;

            private void IncrementCount() => currentCount++;
        }

        """.ReplaceLineEndings());

    public static readonly InitialCode MqttProgram = new("Program.cs", """"
        #:sdk Microsoft.NET.Sdk.Web
        #:package MQTTnet.AspNetCore@5.2.0.1603
        // An MQTT broker in this browser tab, with a public address through a Portal relay (see the Run output).
        // Clients connect over WebSockets at wss://<public address>/mqtt, and the page at / is a small chat on MQTT.js.
        // The browser has no TCP sockets, so there is no plain MQTT listener (port 1883).
        // ⚠️ MQTTnet needs two workarounds in the browser, see MqttBrowser.cs.

        using MQTTnet;
        using MQTTnet.AspNetCore;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHostedMqttServer(MqttBrowser.CreateServerOptions()); // Instead of AddMqttServer().
        builder.Services.AddMqttConnectionHandler();
        builder.Services.AddConnections();

        var app = builder.Build();
        MqttBrowser.DisableKeepAliveMonitor(app.Services);

        app.MapMqtt("/mqtt");
        app.UseMqttServer(server =>
        {
            server.ClientConnectedAsync += e =>
            {
                Console.WriteLine($"Connected: {e.ClientId}");
                return Task.CompletedTask;
            };
            server.InterceptingPublishAsync += e =>
            {
                Console.WriteLine($"{e.ClientId} → {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
                return Task.CompletedTask;
            };
        });
        app.MapGet("/", () => Results.Content(ChatPage.Html, "text/html"));

        app.Run();

        static class ChatPage
        {
            public const string Html = """
                <!doctype html>
                <html lang="en">
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>MQTT chat</title>
                <style>
                  body { font: 16px system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; }
                  #log { list-style: none; padding: 0; }
                  #log li { padding: .4rem 0; border-bottom: 1px solid #ddd; }
                </style>
                <h1>MQTT chat</h1>
                <p id="status">Connecting…</p>
                <form id="form"><input id="text" autocomplete="off" placeholder="Say something" required> <button>Send</button></form>
                <ul id="log"></ul>
                <script src="https://unpkg.com/mqtt@5/dist/mqtt.min.js"></script>
                <script>
                  const name = 'guest-' + Math.random().toString(36).slice(2, 6);
                  const client = mqtt.connect(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/mqtt`);
                  client.on('connect', () => {
                    document.getElementById('status').textContent = `Connected as ${name}`;
                    client.subscribe('chat');
                  });
                  client.on('message', (topic, payload) => {
                    const item = document.createElement('li');
                    item.textContent = payload.toString();
                    document.getElementById('log').prepend(item);
                  });
                  document.getElementById('form').onsubmit = (e) => {
                    e.preventDefault();
                    const input = document.getElementById('text');
                    client.publish('chat', `${name}: ${input.value}`);
                    input.value = '';
                  };
                </script>
                """;
        }

        """".ReplaceLineEndings());

    public static readonly InitialCode MqttBrowser = new("MqttBrowser.cs", """
        using System.Reflection;
        using System.Runtime.CompilerServices;
        using MQTTnet.Server;

        /// <summary>
        /// Workarounds for running the MQTTnet 5 server on the browser's runtime, which has neither sockets nor threads.
        /// Both reach into MQTTnet internals, so they can break with another MQTTnet version.
        /// </summary>
        static class MqttBrowser
        {
            /// <summary>
            /// Like <c>new MqttServerOptionsBuilder().Build()</c>, but without running the constructor of <see cref="MqttServerOptions"/>.
            /// That constructor creates options of the TCP endpoints, whose <c>LingerState = new LingerOption(true, 0)</c>
            /// throws <see cref="PlatformNotSupportedException"/> in the browser although no TCP endpoint is used.
            /// So the options are created uninitialized and their property initializers are repeated here.
            /// </summary>
            public static MqttServerOptions CreateServerOptions()
            {
                var options = (MqttServerOptions)RuntimeHelpers.GetUninitializedObject(typeof(MqttServerOptions));

                // Get-only properties are set through their backing fields.
                SetBackingField(options, nameof(MqttServerOptions.DefaultEndpointOptions), RuntimeHelpers.GetUninitializedObject(typeof(MqttServerTcpEndpointOptions)));
                SetBackingField(options, nameof(MqttServerOptions.TlsEndpointOptions), RuntimeHelpers.GetUninitializedObject(typeof(MqttServerTlsTcpEndpointOptions)));
                SetBackingField(options, nameof(MqttServerOptions.KeepAliveOptions), new MqttServerKeepAliveOptions());

                options.DefaultCommunicationTimeout = TimeSpan.FromSeconds(100);
                options.MaxPendingMessagesPerClient = 1000;
                options.PendingMessagesOverflowStrategy = MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage;
                options.WriterBufferSize = 4096;
                options.WriterBufferSizeMax = 65535;
                return options;
            }

            /// <summary>
            /// The keep-alive monitor loops with <see cref="Thread.Sleep(TimeSpan)"/> in a "long running" task.
            /// The browser has a single thread, which that loop would block forever, freezing everything.
            /// Swapping the monitor for an uninitialized one makes its loop fail on the first iteration and stop,
            /// so clients that stop sending keep-alive pings are not disconnected.
            /// </summary>
            public static void DisableKeepAliveMonitor(IServiceProvider services)
            {
                var server = services.GetRequiredService<MqttServer>();
                var field = typeof(MqttServer).GetField("_keepAliveMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(server, RuntimeHelpers.GetUninitializedObject(field.FieldType));
            }

            static void SetBackingField(object target, string property, object value)
            {
                target.GetType()
                    .GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(target, value);
            }
        }

        """.ReplaceLineEndings());

    public static readonly InitialCode WatsonProgram = new("Program.cs", """
        #:package Watson@7.3.1
        // Watson Webserver in this browser tab, with a public address through a Portal relay (see the Run output).
        // Watson listens on TCP sockets, which the browser does not have,
        // so `server.Start()` is replaced with `server.ServeInBrowser()` from WatsonBrowser.cs.
        // 💡 TIP: Choose the relay with `#:property PortalRelay=https://...`.

        using System;
        using System.Threading.Tasks;
        using WatsonWebserver;
        using WatsonWebserver.Core;

        var settings = new WebserverSettings("localhost", 8000);
        var server = new Webserver(settings, DefaultRoute);

        server.Get("/hello/{name}", async req => new { Greeting = $"Hello, {req.Parameters["name"]}!" });
        server.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/time", async ctx => await ctx.Response.Send(DateTime.UtcNow.ToString("O")));

        server.ServeInBrowser(); // Instead of server.Start().

        static async Task DefaultRoute(HttpContextBase ctx)
        {
            await ctx.Response.Send("Hello from Watson Webserver running in a browser tab!");
        }

        """.ReplaceLineEndings());

    public static readonly InitialCode WatsonBrowser = new("WatsonBrowser.cs", """
        using System;
        using System.Collections.Generic;
        using System.Collections.Specialized;
        using System.IO;
        using System.Reflection;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using WatsonWebserver;
        using WatsonWebserver.Core;

        /// <summary>
        /// Serves a Watson <see cref="Webserver"/> without sockets: DotNetLab hands over each HTTP request
        /// of the public address, and it runs through Watson's routing in an in-memory context.
        /// WebSocket routes and streaming (chunked responses, server-sent events) are not supported.
        /// </summary>
        static class WatsonBrowser
        {
            public static void ServeInBrowser(this Webserver server)
            {
                // DotNetLab exposes a server once its request handler is handed over here:
                // (method, URL, header name/value pairs) => (status, header name/value pairs, body).
                var serve = AppContext.GetData("DotNetLab.WebServer.Serve") as Action<Func<string, string, string[], byte[], Task<(int, string[], byte[])>>>
                    ?? throw new InvalidOperationException("Run this in DotNetLab.");

                // Watson's routing pipeline is protected: normally only its socket loop calls it.
                var process = typeof(WebserverBase).GetMethod("ProcessHttpContextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

                serve(async (method, url, headers, body) =>
                {
                    var response = new MemoryResponse();
                    var context = new HttpContextBase
                    {
                        Request = new MemoryRequest(method, url, headers, body),
                        Response = response,
                    };
                    await (Task)process.Invoke(server, [context, "[Webserver] ", CancellationToken.None])!;

                    var responseHeaders = new List<string>();
                    foreach (string name in response.Headers)
                    {
                        foreach (var value in response.Headers.GetValues(name) ?? [])
                        {
                            responseHeaders.AddRange([name, value]);
                        }
                    }
                    if (!string.IsNullOrEmpty(response.ContentType) && response.Headers["Content-Type"] == null)
                    {
                        responseHeaders.AddRange(["Content-Type", response.ContentType]);
                    }

                    return (response.StatusCode, responseHeaders.ToArray(), response.DataAsBytes);
                });
            }

            sealed class MemoryRequest : HttpRequestBase
            {
                readonly byte[] body;

                public MemoryRequest(string method, string url, string[] headers, byte[] body)
                {
                    this.body = body;
                    var collection = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i + 1 < headers.Length; i += 2)
                    {
                        collection.Add(headers[i], headers[i + 1]);
                    }

                    var host = collection["Host"] ?? "localhost";
                    MethodRaw = method;
                    Method = Enum.TryParse<HttpMethod>(method, ignoreCase: true, out var parsed) ? parsed : HttpMethod.GET;
                    Url = new UrlDetails("https://" + host + url, url);
                    Query = new QueryDetails("https://" + host + url);
                    Headers = collection;
                    Source = new SourceDetails("127.0.0.1", 0);
                    Destination = new DestinationDetails("127.0.0.1", 443, host);
                    ContentType = collection["Content-Type"];
                    ContentLength = body.Length;
                    Useragent = collection["User-Agent"];
                    Data = new MemoryStream(body);
                }

                public override Stream Data { get; set; }
                public override byte[] DataAsBytes => body;
                public override string DataAsString => Encoding.UTF8.GetString(body);
                public override Task<Chunk> ReadChunk(CancellationToken token = default) => throw new NotSupportedException("Chunked requests are not supported.");
                public override bool HeaderExists(string key) => Headers[key] != null;
                public override bool QuerystringExists(string key) => Query.Elements[key] != null;
                public override string RetrieveHeaderValue(string key) => Headers[key];
                public override string RetrieveQueryValue(string key) => Query.Elements[key];
            }

            sealed class MemoryResponse : HttpResponseBase
            {
                readonly MemoryStream body = new();

                public override string DataAsString => Encoding.UTF8.GetString(body.ToArray());
                public override byte[] DataAsBytes => body.ToArray();
                public override MemoryStream Data => body;

                public override Task<bool> Send(CancellationToken token = default) => Complete();
                public override Task<bool> Send(long contentLength, CancellationToken token = default) => Complete();
                public override Task<bool> Send(string data, CancellationToken token = default) => Send(Encoding.UTF8.GetBytes(data ?? ""), token);

                public override Task<bool> Send(byte[] data, CancellationToken token = default)
                {
                    body.Write(data ?? []);
                    return Complete();
                }

                public override async Task<bool> Send(long contentLength, Stream stream, CancellationToken token = default)
                {
                    if (stream != null)
                    {
                        await stream.CopyToAsync(body, token);
                    }
                    return await Complete();
                }

                // The response goes out as a whole, so chunks and events arrive together when it completes.
                public override Task<bool> SendChunk(byte[] chunk, bool isFinal, CancellationToken token = default)
                {
                    body.Write(chunk ?? []);
                    return isFinal ? Complete() : Task.FromResult(true);
                }

                public override Task<bool> SendEvent(ServerSentEvent sse, bool isFinal, CancellationToken token = default)
                {
                    body.Write(Encoding.UTF8.GetBytes(sse?.ToEventString() ?? ""));
                    return isFinal ? Complete() : Task.FromResult(true);
                }

                Task<bool> Complete()
                {
                    ResponseSent = true;
                    return Task.FromResult(true);
                }
            }
        }

        """.ReplaceLineEndings());
}
