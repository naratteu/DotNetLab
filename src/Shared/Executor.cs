using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection.Metadata;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Handler = System.Func<string, string, string[], byte[], System.Threading.Tasks.Task<(int, string[], byte[])>>;
using SocketOpener = System.Func<string, string[], string[], System.Action<bool, byte[]>, System.Action<int, string>,
    System.Threading.Tasks.Task<(string?, System.Func<bool, byte[], System.Threading.Tasks.Task>, System.Func<int, string, System.Threading.Tasks.Task>)>>;

namespace DotNetLab;

public static class Executor
{
    public static async Task<string> ExecuteAsync(
        MemoryStream emitStream,
        ImmutableArray<RefAssembly> assemblies,
        Func<object?, string>? formatScriptReturnValue = null)
    {
        // A server from the previous run would otherwise keep answering.
        await WebServer.StopAsync();

        var alc = new ExecutorLoader(assemblies);
        bool keepLoaded = false;
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            var entryPoint = assembly.EntryPoint
                ?? throw new ArgumentException("No entry point found in the assembly.");

            // Programs using `#:sdk Microsoft.NET.Sdk.Web` signal when their server has started.
            TaskCompletionSource<(Handler, SocketOpener)>? serverStarted = null;
            if (assembly.GetType(WebServer.ShimTypeName) != null)
            {
                serverStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                AppContext.SetData(WebServer.StartedKey, new Action<Handler, SocketOpener>(
                    (handler, socketOpener) => serverStarted.TrySetResult((handler, socketOpener))));
                WebServer.Prepare();
            }

            int exitCode = 0;
            (string stdout, string stderr) = await Util.CaptureConsoleOutputAsync(
                async () =>
                {
                    // Clear the SynchronizationContext so user code that blocks
                    // on async (e.g., `.GetAwaiter().GetResult()`) does not deadlock.
                    // Without this, continuations from e.g. `Task.Yield()` would try
                    // to marshal back to Blazor's single-threaded renderer context.
                    var previousContext = SynchronizationContext.Current;
                    SynchronizationContext.SetSynchronizationContext(null);
                    try
                    {
                        var entryPointTask = InvokeEntryPointCoreAsync(entryPoint);

                        if (serverStarted != null &&
                            await WaitForServerAsync(entryPointTask, serverStarted.Task) is (var handler, var socketOpener))
                        {
                            // The server keeps running after this run reports its output.
                            keepLoaded = true;
                            WebServer.Adopt(handler, socketOpener, alc);
                            var relay = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                .FirstOrDefault(static a => a.Key == WebServer.RelayMetadataKey)?.Value;
                            var url = await WebServer.ExposeAsync(relay);
                            Console.WriteLine($"info: DotNetLab[0]{Environment.NewLine}      Now listening on: {url}");
                            return;
                        }

                        var result = await entryPointTask;
                        exitCode = result.ExitCode;

                        if (result is { IsScriptReturnValue: true } && formatScriptReturnValue != null)
                        {
                            Console.WriteLine(formatScriptReturnValue(result.ReturnValue));
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"Unhandled exception. {e}");
                        exitCode = unchecked((int)0xE0434352);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                    }
                });

            return $"Exit code: {exitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}";
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
        finally
        {
            if (!keepLoaded)
            {
                AppContext.SetData(WebServer.StartedKey, null);
                AppContext.SetData(WebServer.RunningKey, null);
                AppContext.SetData(WebServer.AssetsKey, null);
                if (AppContext.GetData(WebServer.StopKey) is Func<Task> stop)
                {
                    AppContext.SetData(WebServer.StopKey, null);
                    await stop();
                }

                alc.Unload();
            }
        }
    }

    /// <returns>
    /// The request and WebSocket handlers of the started server or <see langword="null"/> if the program finished without one.
    /// </returns>
    private static async Task<(Handler, SocketOpener)?> WaitForServerAsync(
        Task entryPoint,
        Task<(Handler, SocketOpener)> started)
    {
        await Task.WhenAny(entryPoint, started);

        // An intercepted `app.Run()` returns while the server is still starting.
        if (!started.IsCompleted && entryPoint.IsCompletedSuccessfully && AppContext.GetData(WebServer.RunningKey) is Task running)
        {
            await Task.WhenAny(started, running);

            if (!started.IsCompleted)
            {
                // Let the failure surface as an unhandled exception of the program.
                await running;
            }
        }

        // A synchronous `app.Run()` blocks on the server task, which the single-threaded browser does not support,
        // but the server it started keeps running.
        if (!started.IsCompleted && isBlockingRun(entryPoint))
        {
            await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(30)));
        }

        if (!started.IsCompletedSuccessfully)
        {
            return null;
        }

        if (entryPoint.IsFaulted && !isBlockingRun(entryPoint))
        {
            // Let the caller report the failure.
            return null;
        }

        // Observe the expected exception.
        _ = entryPoint.Exception;

        return started.Result;

        static bool isBlockingRun(Task task)
        {
            return task.IsFaulted && task.Exception.InnerException is PlatformNotSupportedException;
        }
    }

    public static async Task<int> InvokeEntryPointAsync(MethodInfo entryPoint)
    {
        return (await InvokeEntryPointCoreAsync(entryPoint)).ExitCode;
    }

    private static async Task<EntryPointResult> InvokeEntryPointCoreAsync(MethodInfo entryPoint)
    {
        (object? target, entryPoint, bool isScript) = getEntryPoint(entryPoint);
        var parameters = entryPoint.GetParameters().Length == 0
            ? null
            : new object[] { Array.Empty<string>() };

        object? @return;
        try
        {
            @return = entryPoint.Invoke(target, parameters);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw ex.InnerException;
        }

        var hasReturnValue = entryPoint.ReturnType != typeof(void);
        switch (@return)
        {
            case Task<int> taskInt:
                @return = await taskInt;
                break;
            case Task<object> taskObject:
                @return = await taskObject;
                break;
            case Task task:
                await task;
                @return = 0;
                hasReturnValue = false;
                break;
            case ValueTask<int> valueTaskInt:
                @return = await valueTaskInt;
                break;
            case ValueTask valueTask:
                await valueTask;
                @return = 0;
                hasReturnValue = false;
                break;
        }

        return new(
            ExitCode: !isScript && @return is int e ? e : 0,
            ReturnValue: @return,
            IsScriptReturnValue: isScript && hasReturnValue);

        // Obtains the async Main method if available because we cannot run the synchronous Main method
        // (because it uses Task.Wait which is unsupported in browser wasm).
        static (object? Target, MethodInfo Method, bool IsScript) getEntryPoint(MethodInfo main)
        {
            try
            {
                var bytes = main.GetMethodBody()?.GetILAsByteArray();
                (object? Target, MethodBase? Method, bool IsScript) invoke = bytes switch
                {
                    // Patterns for async Main method with arguments
                    [
                        (byte)ILOpCode.Ldarg_0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(2, 4))), false),
                    // Patterns for async Main method without arguments
                    [
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(1, 4))), false),
                    // Patterns for AsyncHelpers.HandleAsyncEntryPoint with arguments
                    [
                        (byte)ILOpCode.Ldarg_0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(2, 4))), false),
                    // Patterns for AsyncHelpers.HandleAsyncEntryPoint without arguments
                    [
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Ret
                    ] => (null, main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(1, 4))), false),
                    // Patterns for async Script Main method
                    [
                        (byte)ILOpCode.Newobj, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Callvirt, _, _, _, _,
                        (byte)ILOpCode.Stloc_0,
                        (byte)ILOpCode.Ldloca_s, 0,
                        (byte)ILOpCode.Call, _, _, _, _,
                        (byte)ILOpCode.Pop,
                        (byte)ILOpCode.Ret
                    ] => CreateScriptMain(main, bytes),
                    _ => (null, null, false),
                };
                static (object? Target, MethodInfo? Method, bool IsScript) CreateScriptMain(MethodInfo main, byte[] bytes)
                {
                    ConstructorInfo? constructor = main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(1, 4))) as ConstructorInfo;
                    MethodInfo? initialize = main.Module.ResolveMethod(BitConverter.ToInt32(bytes.AsSpan(6, 4))) as MethodInfo;
                    if (constructor == null || initialize == null) { return (null, null, false); }

                    object instance;

                    try
                    {
                        instance = constructor.Invoke(null);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        ExceptionDispatchInfo.Throw(ex.InnerException);
                        throw ex.InnerException;
                    }

                    return (instance, initialize, true);
                }
                if (invoke.Method is MethodInfo { ReturnType: Type type } info && (type == typeof(Task) || type.IsSubclassOf(typeof(Task))))
                {
                    return (invoke.Target, info, invoke.IsScript);
                }
            }
            catch { }
            return (null, main, false);
        }
    }

    private readonly record struct EntryPointResult(int ExitCode, object? ReturnValue, bool IsScriptReturnValue);

    public static async Task<string> RenderComponentToHtmlAsync(MemoryStream emitStream, string componentTypeName)
    {
        var alc = new AssemblyLoadContext(nameof(RenderComponentToHtmlAsync), isCollectible: true);
        try
        {
            var assembly = alc.LoadFromStream(emitStream);
            var componentType = assembly.GetType(componentTypeName)
                ?? throw new InvalidOperationException($"Cannot find component '{componentTypeName}' in the assembly.");

            var configureServicesMethod = assembly.GetType("Startup")?
                .GetMethod("ConfigureServices", BindingFlags.Public | BindingFlags.Static, [typeof(IServiceCollection)]);

            var services = new ServiceCollection();
            services.AddLogging();
            configureServicesMethod?.Invoke(null, [services]);
            var serviceProvider = services.BuildServiceProvider();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            using var renderer = new HtmlRenderer(serviceProvider, loggerFactory);
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await renderer.RenderComponentAsync(componentType);
                return output.ToHtmlString();
            });
            return html;
        }
        finally
        {
            alc.Unload();
        }
    }
}

public sealed class ExecutorLoader(ImmutableArray<RefAssembly> assemblies) : AssemblyLoadContext(nameof(ExecutorLoader), isCollectible: true)
{
    private readonly Dictionary<string, Assembly> loadedAssemblies = new();
    private readonly IReadOnlyDictionary<string, ImmutableArray<byte>> lookup = assemblies
        .Where(a => a.LoadForExecution)
        .ToDictionary(a => a.Name, a => a.Bytes);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name)
        {
            if (loadedAssemblies.TryGetValue(name, out var loaded))
            {
                return loaded;
            }

            if (lookup.TryGetValue(name, out var bytes))
            {
                var array = ImmutableCollectionsMarshal.AsArray(bytes)!;
                loaded = LoadFromStream(new MemoryStream(array));
                loadedAssemblies.Add(name, loaded);
                return loaded;
            }

            loaded = Default.LoadFromAssemblyName(assemblyName);
            loadedAssemblies.Add(name, loaded);
            return loaded;
        }

        return null;
    }
}
