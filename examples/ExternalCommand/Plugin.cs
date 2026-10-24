using AnchorChain;

namespace SeaPowerAIAdvisor.ExampleCommand;

// Anchor Chain discovers public types before sorting entry points. Keep SDK-dependent types internal.
[ACPlugin("example.seapower.aiadvisor.command", "AI Advisor external command example", "1.0.0",
    new string[0], new[] { "mebiustos.seapower.aiadvisor" })]
[ACDependency("mebiustos.seapower.aiadvisor", null, null)]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint() => CommandRegistration.Register();
}
