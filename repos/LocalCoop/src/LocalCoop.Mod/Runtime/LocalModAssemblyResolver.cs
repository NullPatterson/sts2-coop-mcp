using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace LocalCoop.Mod.Runtime;

public static class LocalModAssemblyResolver
{
    private static readonly object Gate = new();
    private static bool _installed;
    private static string? _modDirectory;
    private static AssemblyLoadContext? _loadContext;

    // The mod loader scans types before calling the initializer, so dependency resolution must be available at module load.
#pragma warning disable CA2255
    [ModuleInitializer]
    public static void InstallForModuleLoad()
#pragma warning restore CA2255
    {
        Install(typeof(LocalModAssemblyResolver).Assembly);
    }

    public static void Install(Assembly modAssembly)
    {
        var modDirectory = Path.GetDirectoryName(modAssembly.Location);
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return;
        }

        lock (Gate)
        {
            _modDirectory = modDirectory;
            _loadContext = AssemblyLoadContext.GetLoadContext(modAssembly);
            if (_installed)
            {
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAppDomain;
            if (_loadContext is not null)
            {
                _loadContext.Resolving += ResolveFromLoadContext;
            }

            _installed = true;
        }
    }

    public static string? ResolveAssemblyPath(string modDirectory, AssemblyName requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName.Name))
        {
            return null;
        }

        var candidatePath = Path.Combine(modDirectory, requestedName.Name + ".dll");
        return File.Exists(candidatePath) ? candidatePath : null;
    }

    private static Assembly? ResolveFromAppDomain(object? sender, ResolveEventArgs args)
    {
        return Resolve(_loadContext ?? AssemblyLoadContext.Default, new AssemblyName(args.Name));
    }

    private static Assembly? ResolveFromLoadContext(AssemblyLoadContext context, AssemblyName requestedName)
    {
        return Resolve(context, requestedName);
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName requestedName)
    {
        var modDirectory = _modDirectory;
        if (string.IsNullOrWhiteSpace(modDirectory))
        {
            return null;
        }

        var assemblyPath = ResolveAssemblyPath(modDirectory, requestedName);
        return assemblyPath is null ? null : context.LoadFromAssemblyPath(assemblyPath);
    }
}
