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
                    runtimeIdentifier: WebSdk.RuntimePackIdentifier);
                var testHostTask = downloader.DownloadAsync(
                    new Set<NuGetDependency>(new HashSet<NuGetDependency>
                    {
                        new() { PackageId = "Microsoft.AspNetCore.TestHost", VersionRange = WebSdk.RuntimeVersionRange },
                    }),
                    WebSdk.RuntimeTargetFramework,
                    loadForExecution: true);

                var runtime = await runtimeTask;
                var testHost = await testHostTask;

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
                using System.Threading;
                using System.Threading.Tasks;
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
                        if (AppContext.GetData("{{WebServer.StartedKey}}") is not Action<{{WebServer.HandlerType}}>)
                        {
                            return;
                        }

                        AppContext.SetData("{{WebServer.StopKey}}", new Func<Task>(StopAsync));
                        subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
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
                                AppContext.GetData("{{WebServer.StartedKey}}") is Action<{{WebServer.HandlerType}}> started)
                            {
                                started((method, url, headers, body) => HandleAsync(server, method, url, headers, body));
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
