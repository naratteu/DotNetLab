using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetLab;

internal abstract partial class FileLevelDirective
{
    /// <summary>
    /// <c>#:sdk Microsoft.NET.Sdk.Web</c> makes the program an ASP.NET Core app: the shared framework
    /// is downloaded for execution and the host is redirected to an in-memory server
    /// (see <see cref="WebSdk.HostingShim"/>) which <see cref="Executor"/> can expose publicly.
    /// </summary>
    public sealed class Sdk : Pair<Sdk>, IPairFileLevelDirective
    {
        public static new IPairDescriptor Descriptor { get; } = new PairDescriptor()
        {
            DirectiveKind = "sdk",
            Parse = Parse,
            Separator = '@',
            SuggestNames = Constant([WebSdk.DefaultSdk, WebSdk.WebSdkName]),
            SuggestValues = static (_, _) => [],
        };

        private Sdk(ParseInfo info) : base(info) { }

        private static Sdk Parse(ParseInfo info) => Parse(info, static (info, t) =>
        {
            return new(info)
            {
                Name = t.Name,
                Value = t.Value,
            };
        });

        public override async ValueTask ConsumeAsync(ConsumerContext context)
        {
            var name = Name.Span;

            if (name.Equals(WebSdk.DefaultSdk, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!name.Equals(WebSdk.WebSdkName, StringComparison.OrdinalIgnoreCase))
            {
                Info.Errors.Add($"Unsupported SDK '{Name}'. Supported SDKs are '{WebSdk.DefaultSdk}' and '{WebSdk.WebSdkName}'.");
                return;
            }

            context.Config.AdditionalSources(static sources => sources.AddRange(WebSdk.ImplicitUsings, WebSdk.HostingShim));
            // All syntax trees must have the same features, so the interceptors namespace is enabled up front.
            context.Config.CSharpParseOptions(WebSdk.EnableInterceptors);
            context.Config.CSharpCompilation(WebSdk.InterceptBlockingRuns);

            try
            {
                var downloader = context.Services.GetRequiredService<INuGetDownloader>();

                // The runtime pack has the implementation of the shared framework whose reference assemblies are built in.
                // Its R2R code is for another platform, but the IL next to it runs fine on the browser.
                var runtimeTask = downloader.DownloadAsync(
                    new Set<NuGetDependency>(new HashSet<NuGetDependency>
                    {
                        new() { PackageId = $"Microsoft.AspNetCore.App.Runtime.{WebSdk.RuntimePackIdentifier}", VersionRange = WebSdk.RuntimeVersionRange },
                    }),
                    WebSdk.RuntimeTargetFramework,
                    loadForExecution: true,
                    folder: new($"runtimes/{WebSdk.RuntimePackIdentifier}/lib/{WebSdk.RuntimeTargetFramework}", ".dll"));
                var testHostTask = downloader.DownloadAsync(
                    new Set<NuGetDependency>(new HashSet<NuGetDependency>
                    {
                        new() { PackageId = "Microsoft.AspNetCore.TestHost", VersionRange = WebSdk.RuntimeVersionRange },
                    }),
                    WebSdk.RuntimeTargetFramework,
                    loadForExecution: true);
                // Blazor scripts like `_framework/blazor.web.js` which are normally served from the build output.
                var assetsTask = downloader.DownloadAsync(
                    new Set<NuGetDependency>(new HashSet<NuGetDependency>
                    {
                        new() { PackageId = "Microsoft.AspNetCore.App.Internal.Assets", VersionRange = WebSdk.RuntimeVersionRange },
                    }),
                    WebSdk.RuntimeTargetFramework,
                    loadForExecution: false,
                    folder: new("_framework", ".js"));

                var runtime = await runtimeTask;
                var testHost = await testHostTask;
                var assets = await assetsTask;

                if (assets.Errors.Count > 0)
                {
                    context.Logger.LogWarning("Could not download Blazor scripts: {Errors}", assets.Errors.Values.SelectMany(e => e).JoinToString("; "));
                }

                WebServer.FrameworkAssets = assets.Assemblies.IsDefaultOrEmpty
                    ? ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                    : assets.Assemblies.ToImmutableDictionary(static a => $"_framework/{a.Name}.js", static a => a.Bytes);

                foreach (var errors in runtime.Errors.Values.Concat(testHost.Errors.Values))
                {
                    Info.Errors.AddRange(errors);
                }

                if (runtime.Assemblies.IsDefaultOrEmpty || testHost.Assemblies.IsDefaultOrEmpty)
                {
                    Info.Errors.Add("Could not download the ASP.NET Core runtime.");
                    return;
                }

                // Compile against the built-in reference assemblies but execute the implementation ones.
                context.Config.References(list => WebSdk.UseForExecution(list, runtime.Assemblies));
                context.Config.AdditionalReferences(() => new()
                {
                    Assemblies = testHost.Assemblies,
                    Metadata = RefAssemblyMetadata.Create(testHost.Assemblies),
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex, "Failed to download the ASP.NET Core runtime.");
                Info.Errors.Add($"Failed to download the ASP.NET Core runtime: {ex.Message.GetFirstLine()}");
            }
        }
    }
}

internal static class WebSdk
{
    public const string DefaultSdk = "Microsoft.NET.Sdk";
    public const string WebSdkName = "Microsoft.NET.Sdk.Web";

    /// <summary>
    /// Any runtime pack works since only its IL is used.
    /// </summary>
    public const string RuntimePackIdentifier = "linux-x64";

    /// <summary>
    /// The ASP.NET Core which executes the program.
    /// It cannot match the running .NET 11: its runtime pack is built for CoreCLR with runtime async
    /// (methods with the <c>MethodImplOptions.Async</c> flag),
    /// which the Mono runtime in the browser cannot execute (<see cref="InvalidProgramException"/>).
    /// .NET 10 builds have no such methods and run on the newer runtime.
    /// The program is still compiled against the built-in (newer) reference assemblies.
    /// </summary>
    public const string RuntimeVersionRange = "10.0.*";

    public const string RuntimeTargetFramework = "net10.0";

    public static RefAssemblyList UseForExecution(RefAssemblyList list, ImmutableArray<RefAssembly> implementations)
    {
        var byName = implementations.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var assemblies = list.Assemblies.ToBuilder();
        var metadata = list.Metadata.ToBuilder();

        for (int i = 0; i < assemblies.Count; i++)
        {
            if (byName.Remove(assemblies[i].Name, out var implementation))
            {
                assemblies[i] = implementation;
            }
        }

        // Implementation assemblies without a reference counterpart are not referenced by user code
        // but can be needed by the framework at run time.
        foreach (var implementation in byName.Values)
        {
            assemblies.Add(implementation);
            metadata.AddRange(RefAssemblyMetadata.Create([implementation]));
        }

        return new()
        {
            Assemblies = assemblies.DrainToImmutable(),
            Metadata = metadata.DrainToImmutable(),
        };
    }

    public static CSharpParseOptions EnableInterceptors(CSharpParseOptions options)
    {
        const string key = "InterceptorsNamespaces";
        var namespaces = options.Features.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? $"{existing};DotNetLab.Generated"
            : "DotNetLab.Generated";
        return options.WithFeatures([.. options.Features.Where(p => p.Key != key), new(key, namespaces)]);
    }

    /// <summary>
    /// <c>app.Run()</c> blocks until the server stops, but blocking is not possible on the single-threaded browser
    /// (it hangs the whole worker). So such calls are intercepted to start the server without waiting for it;
    /// the server outlives the program anyway (see <see cref="Executor"/>).
    /// </summary>
    public static CSharpCompilation InterceptBlockingRuns(CSharpCompilation compilation)
    {
        var webApplicationRun = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Builder.WebApplication")?.GetMembers("Run");
        var hostRun = compilation.GetTypeByMetadataName("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions")?.GetMembers("Run");
        if (webApplicationRun is not { Length: > 0 } || hostRun is not { Length: > 0 })
        {
            return compilation;
        }

        var webApplicationLocations = new StringBuilder();
        var hostLocations = new StringBuilder();

        try
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                    {
                        continue;
                    }

                    var definition = (method.ReducedFrom ?? method).OriginalDefinition;
                    var locations = webApplicationRun.Value.Contains(definition, SymbolEqualityComparer.Default) ? webApplicationLocations
                        : hostRun.Value.Contains(definition, SymbolEqualityComparer.Default) ? hostLocations
                        : null;
#pragma warning disable RSEXPERIMENTAL002 // Interceptors are experimental
                    if (locations != null && model.GetInterceptableLocation(invocation) is { } location)
                    {
                        locations.AppendLine($"        {location.GetInterceptsLocationAttributeSyntax()}");
                    }
#pragma warning restore RSEXPERIMENTAL002
                }
            }
        }
        catch (MissingMethodException)
        {
            // An older compiler without the interceptors API is selected.
            return compilation;
        }

        if (webApplicationLocations.Length == 0 && hostLocations.Length == 0)
        {
            return compilation;
        }

        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;

        var text = $$"""
            // <auto-generated/>
            #nullable enable
            #pragma warning disable

            namespace DotNetLab.Generated
            {
                internal static class WebRunInterceptors
                {
            {{webApplicationLocations}}
                    public static void Run(this Microsoft.AspNetCore.Builder.WebApplication app, string? url = null)
                        => WebHostingShim.Running(app.RunAsync(url));

            {{hostLocations}}
                    public static void Run(this Microsoft.Extensions.Hosting.IHost host)
                        => WebHostingShim.Running(Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(host));
                }
            }

            namespace System.Runtime.CompilerServices
            {
                [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                file sealed class InterceptsLocationAttribute : Attribute
                {
                    public InterceptsLocationAttribute(int version, string data) { }
                }
            }

            """;

        return compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(text, parseOptions, path: "WebRunInterceptors.g.cs", encoding: Encoding.UTF8));
    }

    /// <summary>
    /// What <c>ImplicitUsings</c> brings in a web project.
    /// </summary>
    public static SourceFile ImplicitUsings { get; } = new()
    {
        FileName = "WebGlobalUsings.g.cs",
        Text = """
            // <auto-generated/>
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Net.Http.Json;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Microsoft.AspNetCore.Builder;
            global using Microsoft.AspNetCore.Hosting;
            global using Microsoft.AspNetCore.Http;
            global using Microsoft.AspNetCore.Routing;
            global using Microsoft.Extensions.Configuration;
            global using Microsoft.Extensions.DependencyInjection;
            global using Microsoft.Extensions.Hosting;
            global using Microsoft.Extensions.Logging;

            """,
    };

    /// <summary>
    /// Swaps the server of every host the program builds for an in-memory <c>TestServer</c>
    /// (like <c>WebApplicationFactory</c> does) and hands its request handler to <see cref="WebServer"/>.
    /// It only uses core library types across the boundary since the program runs in its own load context.
    /// </summary>
    public static SourceFile HostingShim { get; } = new()
    {
        FileName = "WebHostingShim.g.cs",
        Text = $$"""
            // <auto-generated/>
            #nullable enable
            #pragma warning disable

            namespace DotNetLab.Generated
            {
                using System;
                using System.Collections.Generic;
                using System.Diagnostics;
                using System.IO;
                using System.Net.WebSockets;
                using System.Security.Cryptography;
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.DataProtection;
                using Microsoft.AspNetCore.Hosting;
                using Microsoft.AspNetCore.Hosting.Server;
                using Microsoft.AspNetCore.Http;
                using Microsoft.AspNetCore.TestHost;
                using Microsoft.Extensions.DependencyInjection;
                using Microsoft.Extensions.Hosting;
                using Microsoft.Extensions.Logging;

                internal static class WebHostingShim
                {
                    private static readonly List<IHost> hosts = new();
                    private static IDisposable? subscription;

                    [System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Initialize()
                    {
                        if (AppContext.GetData("{{WebServer.StartedKey}}") is not Action<{{WebServer.HandlerType}}, {{WebServer.SocketOpenerType}}>)
                        {
                            return;
                        }

                        AppContext.SetData("{{WebServer.StopKey}}", new Func<Task>(StopAsync));
                        subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());

                        // `MapStaticAssets()` throws without the manifest from the build output.
                        // An empty one makes `@Assets[...]` return paths as they are, which the middleware below serves.
                        try
                        {
                            var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                            var manifest = Path.Combine(AppContext.BaseDirectory, name + ".staticwebassets.endpoints.json");
                            if (name != null && !File.Exists(manifest))
                            {
                                File.WriteAllText(manifest, "{\"Version\":1,\"ManifestType\":\"Build\",\"Endpoints\":[]}");
                            }
                        }
                        catch { }
                    }

                    /// <summary>
                    /// Called instead of a blocking <c>Run()</c>, see <c>WebRunInterceptors</c>.
                    /// </summary>
                    internal static void Running(Task run)
                    {
                        AppContext.SetData("{{WebServer.RunningKey}}", run);
                    }

                    private static async Task StopAsync()
                    {
                        subscription?.Dispose();
                        subscription = null;
                        foreach (var host in hosts)
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            try { await host.StopAsync(timeout.Token); } catch { }
                            host.Dispose();
                        }
                        hosts.Clear();
                    }

                    private static void Configure(IHostBuilder builder)
                    {
                        builder.ConfigureServices(static (_, services) =>
                        {
                            services.AddSingleton<IServer, TestServer>();
                            services.AddSingleton<IHostLifetime, NoopLifetime>();
                            services.AddSingleton<IStartupFilter, FrameworkAssetsStartupFilter>();
                            // The default data protection uses AES, which the browser does not have.
                            services.AddSingleton<IDataProtectionProvider>(new HmacDataProtector(RandomNumberGenerator.GetBytes(32), ""));
                            for (int i = services.Count - 1; i >= 0; i--)
                            {
                                if (services[i].ServiceType == typeof(IHostedService) &&
                                    services[i].ImplementationType?.Name == "DataProtectionHostedService")
                                {
                                    services.RemoveAt(i);
                                }
                            }
                            // The console logger writes from a thread, which the browser does not have.
                            services.AddLogging(static logging =>
                            {
                                logging.ClearProviders();
                                logging.AddProvider(new ConsoleProvider());
                            });
                        });
                    }

                    private static void Track(IHost host)
                    {
                        hosts.Add(host);
                        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
                        {
                            if (host.Services.GetService<IServer>() is TestServer server &&
                                AppContext.GetData("{{WebServer.StartedKey}}") is Action<{{WebServer.HandlerType}}, {{WebServer.SocketOpenerType}}> started)
                            {
                                started(
                                    (method, url, headers, body) => HandleAsync(server, method, url, headers, body),
                                    (url, headers, protocols, onMessage, onClose) => OpenSocketAsync(server, url, headers, protocols, onMessage, onClose));
                            }
                        });
                    }

                    private static async Task<(int, string[], byte[])> HandleAsync(TestServer server, string method, string url, string[] headers, byte[] body)
                    {
                        HttpContext context;
                        try
                        {
                            context = await server.SendAsync(c =>
                            {
                                var request = c.Request;
                                request.Scheme = "https";
                                request.Method = method;
                                int query = url.IndexOf('?');
                                request.Path = PathString.FromUriComponent(query < 0 ? url : url.Substring(0, query));
                                request.QueryString = query < 0 ? QueryString.Empty : QueryString.FromUriComponent(url.Substring(query));
                                for (int i = 0; i + 1 < headers.Length; i += 2)
                                {
                                    if (string.Equals(headers[i], "Host", StringComparison.OrdinalIgnoreCase))
                                    {
                                        request.Host = HostString.FromUriComponent(headers[i + 1]);
                                    }
                                    else
                                    {
                                        request.Headers.Append(headers[i], headers[i + 1]);
                                    }
                                }
                                request.Body = new MemoryStream(body);
                            });
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"fail: {method} {url}{Environment.NewLine}{ex}");
                            return (500, new[] { "Content-Type", "text/plain; charset=utf-8" }, System.Text.Encoding.UTF8.GetBytes(ex.ToString()));
                        }

                        var responseHeaders = new List<string>();
                        foreach (var header in context.Response.Headers)
                        {
                            foreach (var value in header.Value)
                            {
                                responseHeaders.Add(header.Key);
                                responseHeaders.Add(value ?? "");
                            }
                        }

                        using var responseBody = new MemoryStream();
                        await context.Response.Body.CopyToAsync(responseBody);
                        return (context.Response.StatusCode, responseHeaders.ToArray(), responseBody.ToArray());
                    }

                    private static async Task<(string?, Func<bool, byte[], Task>, Func<int, string, Task>)> OpenSocketAsync(
                        TestServer server, string url, string[] headers, string[] protocols, Action<bool, byte[]> onMessage, Action<int, string> onClose)
                    {
                        var host = "localhost";
                        for (int i = 0; i + 1 < headers.Length; i += 2)
                        {
                            if (string.Equals(headers[i], "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                host = headers[i + 1];
                            }
                        }

                        var client = server.CreateWebSocketClient();
                        client.ConfigureRequest = request =>
                        {
                            for (int i = 0; i + 1 < headers.Length; i += 2)
                            {
                                var name = headers[i];
                                // The test client does its own handshake.
                                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, "Upgrade", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
                                    name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                request.Headers.Append(name, headers[i + 1]);
                            }
                        };
                        foreach (var protocol in protocols)
                        {
                            client.SubProtocols.Add(protocol);
                        }

                        var socket = await client.ConnectAsync(new Uri("wss://" + host + url), CancellationToken.None);
                        _ = ReceiveAsync(socket, onMessage, onClose);

                        // WebSocket does not allow concurrent sends.
                        var sending = Task.CompletedTask;
                        Func<bool, byte[], Task> send = (text, data) => sending = SendAfterAsync(sending, socket, text, data);
                        Func<int, string, Task> close = async (code, reason) =>
                        {
                            try
                            {
                                var status = code is >= 1000 and < 5000 and not (1005 or 1006 or 1015) ? (WebSocketCloseStatus)code : WebSocketCloseStatus.NormalClosure;
                                await socket.CloseOutputAsync(status, reason, CancellationToken.None);
                            }
                            catch { }
                        };
                        return (socket.SubProtocol, send, close);
                    }

                    private static async Task SendAfterAsync(Task previous, WebSocket socket, bool text, byte[] data)
                    {
                        try { await previous; } catch { }
                        await socket.SendAsync(new ArraySegment<byte>(data), text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
                    }

                    private static async Task ReceiveAsync(WebSocket socket, Action<bool, byte[]> onMessage, Action<int, string> onClose)
                    {
                        var buffer = new byte[16 * 1024];
                        using var message = new MemoryStream();
                        try
                        {
                            while (true)
                            {
                                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    onClose((int)(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure), socket.CloseStatusDescription ?? "");
                                    return;
                                }

                                message.Write(buffer, 0, result.Count);
                                if (result.EndOfMessage)
                                {
                                    onMessage(result.MessageType == WebSocketMessageType.Text, message.ToArray());
                                    message.SetLength(0);
                                }
                            }
                        }
                        catch
                        {
                            onClose((int)WebSocketCloseStatus.InternalServerError, "");
                        }
                    }

                    private sealed class ListenerObserver : IObserver<DiagnosticListener>
                    {
                        public void OnNext(DiagnosticListener listener)
                        {
                            if (listener.Name == "Microsoft.Extensions.Hosting")
                            {
                                listener.Subscribe(new HostingObserver());
                            }
                        }

                        public void OnCompleted() { }
                        public void OnError(Exception error) { }
                    }

                    // Types of hosts from other runs live in other load contexts, so these type tests filter them out.
                    private sealed class HostingObserver : IObserver<KeyValuePair<string, object?>>
                    {
                        public void OnNext(KeyValuePair<string, object?> e)
                        {
                            if (e.Key == "HostBuilding" && e.Value is IHostBuilder builder)
                            {
                                Configure(builder);
                            }
                            else if (e.Key == "HostBuilt" && e.Value is IHost host)
                            {
                                Track(host);
                            }
                        }

                        public void OnCompleted() { }
                        public void OnError(Exception error) { }
                    }

                    /// <summary>
                    /// Serves static files of the framework (like <c>_framework/blazor.web.js</c>) which are normally in the build output.
                    /// </summary>
                    private sealed class FrameworkAssetsStartupFilter : IStartupFilter
                    {
                        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
                        {
                            var assets = AppContext.GetData("{{WebServer.AssetsKey}}") as Func<string, byte[]?>;
                            app.Use(async (context, nextMiddleware) =>
                            {
                                if (assets != null &&
                                    context.Request.Path.StartsWithSegments("/_framework") &&
                                    assets(context.Request.Path.Value!.TrimStart('/')) is { } bytes)
                                {
                                    context.Response.ContentType = "text/javascript; charset=utf-8";
                                    context.Response.ContentLength = bytes.Length;
                                    await context.Response.Body.WriteAsync(bytes);
                                    return;
                                }

                                await nextMiddleware(context);
                            });
                            next(app);
                        };
                    }

                    /// <summary>
                    /// Encrypts with HMAC-SHA256 as a pseudorandom function in counter mode and authenticates
                    /// with another HMAC-SHA256 key (encrypt-then-MAC). Keys live only as long as the server.
                    /// </summary>
                    private sealed class HmacDataProtector : IDataProtector
                    {
                        private const int NonceSize = 16;
                        private const int TagSize = 32;
                        private readonly byte[] masterKey;
                        private readonly string purposes;
                        private readonly byte[] encryptionKey;
                        private readonly byte[] signingKey;

                        public HmacDataProtector(byte[] masterKey, string purposes)
                        {
                            this.masterKey = masterKey;
                            this.purposes = purposes;
                            encryptionKey = HMACSHA256.HashData(masterKey, System.Text.Encoding.UTF8.GetBytes("encrypt\0" + purposes));
                            signingKey = HMACSHA256.HashData(masterKey, System.Text.Encoding.UTF8.GetBytes("sign\0" + purposes));
                        }

                        public IDataProtector CreateProtector(string purpose) => new HmacDataProtector(masterKey, purposes + "\0" + purpose);

                        public byte[] Protect(byte[] plaintext)
                        {
                            var output = new byte[NonceSize + plaintext.Length + TagSize];
                            RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
                            Xor(output.AsSpan(0, NonceSize), plaintext, output.AsSpan(NonceSize, plaintext.Length));
                            HMACSHA256.HashData(signingKey, output.AsSpan(0, NonceSize + plaintext.Length), output.AsSpan(NonceSize + plaintext.Length));
                            return output;
                        }

                        public byte[] Unprotect(byte[] protectedData)
                        {
                            if (protectedData.Length < NonceSize + TagSize)
                            {
                                throw new CryptographicException("The payload was invalid.");
                            }

                            var signed = protectedData.AsSpan(0, protectedData.Length - TagSize);
                            var tag = HMACSHA256.HashData(signingKey, signed);
                            if (!CryptographicOperations.FixedTimeEquals(tag, protectedData.AsSpan(signed.Length)))
                            {
                                throw new CryptographicException("The payload was invalid.");
                            }

                            var plaintext = new byte[signed.Length - NonceSize];
                            Xor(signed.Slice(0, NonceSize), signed.Slice(NonceSize), plaintext);
                            return plaintext;
                        }

                        private void Xor(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
                        {
                            var block = new byte[NonceSize + 4];
                            nonce.CopyTo(block);
                            var stream = new byte[32];
                            for (int offset = 0, counter = 0; offset < input.Length; offset += stream.Length, counter++)
                            {
                                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(NonceSize), counter);
                                HMACSHA256.HashData(encryptionKey, block, stream);
                                for (int i = 0; i < stream.Length && offset + i < input.Length; i++)
                                {
                                    output[offset + i] = (byte)(input[offset + i] ^ stream[i]);
                                }
                            }
                        }
                    }

                    private sealed class NoopLifetime : IHostLifetime
                    {
                        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                    }

                    private sealed class ConsoleProvider : ILoggerProvider
                    {
                        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
                        public void Dispose() { }
                    }

                    private sealed class ConsoleLogger : ILogger
                    {
                        private readonly string category;

                        public ConsoleLogger(string category) => this.category = category;

                        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

                        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                        {
                            var level = logLevel switch
                            {
                                LogLevel.Trace => "trce",
                                LogLevel.Debug => "dbug",
                                LogLevel.Information => "info",
                                LogLevel.Warning => "warn",
                                LogLevel.Error => "fail",
                                _ => "crit",
                            };
                            var writer = logLevel >= LogLevel.Error ? Console.Error : Console.Out;
                            writer.WriteLine($"{level}: {category}[{eventId.Id}]{Environment.NewLine}      {formatter(state, exception)}");
                            if (exception != null)
                            {
                                writer.WriteLine(exception);
                            }
                        }
                    }
                }
            }

            """,
    };
}
