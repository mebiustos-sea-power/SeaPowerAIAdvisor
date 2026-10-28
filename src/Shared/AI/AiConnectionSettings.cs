using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// Detached values for one provider connection. Features own persistence, selection and supplemental instructions.
internal sealed class AiConnectionSettings
{
    internal string Model { get; }
    internal string ApiKey { get; }
    internal int RequestTimeoutSeconds { get; }
    internal int MaxOutputTokens { get; }
    internal JObject Settings { get; }
    internal bool IsSubConnection { get; }
    internal AiConnectionSettings(string model, string apiKey, int requestTimeoutSeconds, int maxOutputTokens,
        JObject settings, bool isSubConnection = false)
    {
        Model = model; ApiKey = apiKey; RequestTimeoutSeconds = AiRequestTimeout.Validate(requestTimeoutSeconds);
        MaxOutputTokens = AiOutputTokens.Validate(maxOutputTokens); Settings = (JObject)settings.DeepClone(); IsSubConnection = isSubConnection;
    }
}
