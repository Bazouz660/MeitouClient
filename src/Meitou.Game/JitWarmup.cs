using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Meitou.Game;

/// <summary>
/// Compiles the Meitou assemblies' methods ahead of their first call, on a background thread below normal priority from the start of the game (docs/bench.md
/// "Play benchmark"). The game runs without tiering (DECISIONS 19): a method is compiled once, fully optimised, when first called, and in play that landed in
/// frames (the AI's first act, a screen's first binding, 20-80 ms each). Compiled here the code is the same, and the frame finds it ready. Generic methods and
/// methods of generic types are left out (their instantiations are only known when used); so is code already compiled. <c>--no-precompile</c> turns it off.
/// </summary>
static class JitWarmup
{
    public static void Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "jit warm-up" };
        thread.Start();
    }

    static void Run()
    {
        var watch = Stopwatch.StartNew();
        int compiled = 0, failed = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        // The game's own assemblies, the simulation first (its systems are the first called once the world ticks); the rest as they are loaded or referenced.
        var assemblies = typeof(JitWarmup).Assembly.GetReferencedAssemblies().Where(a => a.Name?.StartsWith("Meitou", StringComparison.Ordinal) == true)
            .Select(TryLoad).OfType<Assembly>().Prepend(typeof(JitWarmup).Assembly)
            .OrderBy(a => a.GetName().Name == "Meitou.Simulation" ? 0 : 1).ToList();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = [.. e.Types.OfType<Type>()]; }
            foreach (var type in types)
            {
                if (type.ContainsGenericParameters) continue;
                foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0
                        || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                        continue;
                    try
                    {
                        RuntimeHelpers.PrepareMethod(method.MethodHandle);
                        compiled++;
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or TypeLoadException or BadImageFormatException or MissingMethodException)
                    {
                        failed++;
                    }
                }
            }
        }
        Console.WriteLine($"jit       {compiled} methods compiled ahead in {watch.Elapsed.TotalSeconds:0.0} s{(failed > 0 ? $" ({failed} could not be)" : "")}");
    }

    static Assembly? TryLoad(AssemblyName name)
    {
        try { return Assembly.Load(name); }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { return null; }
    }
}
