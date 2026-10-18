using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AnchorChain;

// Intentionally has no SDK or host reference: discovering this executable cannot preload them.
internal static class Program
{
    private const string HostId = "mebiustos.seapower.aiadvisor";
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static object? catalog;
    private static MethodInfo? register;
    private static int registrations;

    private static int Main(string[] args)
    {
        try
        {
            Require(Type.GetType("Mono.Runtime") != null, "Run with Sea Power's Mono runtime.");
            Require(args.Length == 4, "Expected host, API, extension DLL paths and a discovery order.");
            var paths = new Dictionary<string, string> { ["main"] = args[0], ["api"] = args[1], ["extension"] = args[2] };
            var disabled = args[3] == "disabled";
            var order = disabled ? new[] { "extension", "main", "api" } : args[3].Split('-');
            Require(order.Length == 3 && order.Distinct().Count() == 3 && order.All(paths.ContainsKey), "Invalid discovery order.");
            Require(!ApiLoaded(), "The test preloaded the API before discovery.");
            var loaded = new Dictionary<string, Assembly>();
            var plugins = new List<(ACPlugin Metadata, Type Type, IAnchorChainMod? Instance)>();
            foreach (var name in order)
            {
                var assembly = Assembly.LoadFile(Path.GetFullPath(paths[name]));
                loaded.Add(name, assembly);
                foreach (var type in assembly.GetExportedTypes().Where(t => t.GetInterfaces().Contains(typeof(IAnchorChainMod))))
                {
                    var metadata = (ACPlugin?)Attribute.GetCustomAttribute(type, typeof(ACPlugin));
                    if (metadata == null) continue;
                    _ = Attribute.GetCustomAttributes(type, typeof(ACDependency));
                    // The host's Unity lifecycle is replaced with registry initialization below.
                    var instance = metadata.GUID == HostId ? null : (IAnchorChainMod)Activator.CreateInstance(type)!;
                    plugins.Add((metadata, type, instance));
                }
                if (!loaded.ContainsKey("api")) Require(!ApiLoaded(), name + " required the API during discovery.");
                Console.WriteLine("Discovered " + name);
            }
            var completed = new HashSet<string>(StringComparer.Ordinal);
            while (plugins.Count > 0)
            {
                var index = plugins.FindIndex(p => p.Metadata.After.IsSubsetOf(completed));
                Require(index >= 0, "Entry-point dependencies could not be resolved.");
                var plugin = plugins[index]; plugins.RemoveAt(index);
                if (plugin.Metadata.GUID == HostId)
                {
                    if (!disabled) InitializeRegistry(loaded["main"], loaded["api"]);
                }
                else
                {
                    Require(completed.Contains(HostId), "The extension ran before the host.");
                    plugin.Instance!.TriggerEntryPoint();
                }
                completed.Add(plugin.Metadata.GUID);
            }
            Require(completed.Count == 2, "The host or extension entry point was not discovered.");
            if (disabled) Require(registrations == 0, "An inactive host accepted extension registrations.");
            else
            {
                Require(registrations == 2, "Both extension strategies must register through the public API.");
                var require = catalog!.GetType().GetMethod("Require", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var id in new[] { "gemini", "ollama", "example.standard", "example.independent" })
                    Require(require.Invoke(catalog, new object[] { id }) != null, "Provider missing: " + id);
                loaded["api"].GetType("SeaPowerAIAdvisor.Api.AdvisorApi", true)!.GetMethod("Detach", HiddenStatic)!.Invoke(null, null);
            }
            Console.WriteLine("PASS " + args[3] + ": discovery, ordered entry point, public registration");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static bool ApiLoaded() => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SeaPowerAIAdvisor.Api");
    private static void InitializeRegistry(Assembly host, Assembly api)
    {
        var type = host.GetType("SeaPowerAIAdvisor.AdvisorServiceCatalog", true)!;
        catalog = type.GetProperty("BuiltIn", HiddenStatic)!.GetValue(null);
        register = type.GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var attach = api.GetType("SeaPowerAIAdvisor.Api.AdvisorApi", true)!.GetMethod("Attach", HiddenStatic)!;
        var callback = Delegate.CreateDelegate(attach.GetParameters()[0].ParameterType,
            typeof(Program).GetMethod(nameof(Register), HiddenStatic)!);
        attach.Invoke(null, new object[] { callback });
    }
    private static bool Register(object extension)
    {
        register!.Invoke(catalog, new[] { extension });
        registrations++;
        return true;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
