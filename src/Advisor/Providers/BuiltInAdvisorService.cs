using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class BuiltInAdvisorService : AdvisorService
{
    private readonly AiProviderDefinition definition;
    internal BuiltInAdvisorService(string id)
    { definition = AiProviderDefinition.Require(id); Fields = definition.Fields; }
    internal override string Id => definition.Id;
    internal override string Name => definition.Name;
    internal override string Version => definition.Version;
    internal override string Protocol => definition.Protocol;
    internal override string Summary(AdvisorText text) => text.Format("extensions.adapter_summary", Name);
    internal override string Caption(AdvisorText text) => definition.Caption(key => text[key], text.Language);
    internal override string Description(AdvisorText text) => definition.Description(key => text[key], text.Language);
    internal override IReadOnlyList<AiConnectionField> Fields { get; }
    internal override void Validate(JObject settings) => definition.Validate(settings);
    internal override IAiProvider Open(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options, bool fallback, AiHttpTransport transport) =>
        definition.Open(configuration.ToConnectionSettings(fallback), transport);
    internal override async Task<AdviceResult> TestAsync(AdvisorApiConfiguration configuration, AdvisorConnectionOptions options,
        AiHttpTransport transport, string request, CancellationToken cancellation)
    {
        var result = await definition.TestAsync(configuration.ToConnectionSettings(), transport, request,
            AdvisorPrompts.ToolConnectionTestRequest(), cancellation).ConfigureAwait(false);
        return new AdviceResult(result.Text, result.ApiRequests, 0, result.TotalTokens, model: result.Model);
    }
}
