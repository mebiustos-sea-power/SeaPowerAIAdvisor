using AnchorChain;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor.Example;

[ACPlugin("example.seapower.aiadvisor", "AI Advisor API examples (offline)", "1.0.0", new string[0], new[] { AdvisorApi.HostModId })]
[ACDependency(AdvisorApi.HostModId, null, null)]
public sealed class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint() => ProviderRegistration.Register();
}
