using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// One connection owns its output budget; providers only translate it to their wire format.
internal static class AiOutputTokens
{
    internal const int Default = 8192;
    internal static int Validate(int tokens) => tokens > 0
        ? tokens : throw new AiConnectionException("output_tokens_invalid");
    internal static int Parse(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens)
        ? Validate(tokens) : throw new AiConnectionException("output_tokens_invalid");
    internal static int Read(JObject value)
    {
        var token = value["max_output_tokens"];
        if (token == null) return Default;
        if (token.Type != JTokenType.Integer) throw new AiConnectionException("output_tokens_invalid");
        return Parse(token.ToString());
    }
}
