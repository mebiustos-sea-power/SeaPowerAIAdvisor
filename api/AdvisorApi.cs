using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SeaPowerAIAdvisor")]
[assembly: InternalsVisibleTo("SeaPowerAIAdvisor.Tests")]

namespace SeaPowerAIAdvisor.Api;

// The host binds only after activation. Extensions register from their AnchorChain entry point,
// after the advisor entry point; loading a DLL alone never activates its provider.
public static class AdvisorApi
{
    public const int ContractVersion = 1;
    public const string HostModId = "mebiustos.seapower.aiadvisor";
    private static readonly object Sync = new();
    private static Func<IAdvisorExtension, bool>? register;
    private static Func<IAdvisorOperationExtension, bool>? registerOperation;
    public static bool IsAvailable { get { lock (Sync) return register != null; } }

    public static bool Register(IAdvisorExtension extension)
    {
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        Func<IAdvisorExtension, bool> target;
        lock (Sync) target = register ?? throw new InvalidOperationException("AI Advisor is not active. Register after its AnchorChain entry point.");
        return target(extension);
    }

    public static bool RegisterOperation(IAdvisorOperationExtension extension)
    {
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        Func<IAdvisorOperationExtension, bool> target;
        lock (Sync) target = registerOperation ?? throw new InvalidOperationException("AI Advisor operation registration is not active. Register after its AnchorChain entry point.");
        return target(extension);
    }

    internal static void Attach(Func<IAdvisorExtension, bool> registration) => Attach(registration, null);
    internal static void Attach(Func<IAdvisorExtension, bool> registration, Func<IAdvisorOperationExtension, bool>? operationRegistration)
    {
        if (registration == null) throw new ArgumentNullException(nameof(registration));
        lock (Sync)
        {
            if (register != null) throw new InvalidOperationException("The AI Advisor API is already attached.");
            register = registration; registerOperation = operationRegistration;
        }
    }
    internal static void Detach() { lock (Sync) { register = null; registerOperation = null; } }
}
