using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// A connection owns the deadline for each individual HTTP request, including model loading.
internal static class AdvisorRequestTimeout
{
    internal const int DefaultSeconds = 60;
    internal const int MaximumSeconds = 86400;
    internal static int Validate(int seconds) => seconds >= 1 && seconds <= MaximumSeconds
        ? seconds : throw new AdvisorConnectionException("request_timeout_invalid");
    internal static int Parse(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        ? Validate(seconds) : throw new AdvisorConnectionException("request_timeout_invalid");
    internal static int Read(JObject value, int fallback = DefaultSeconds)
    {
        var token = value["request_timeout_seconds"];
        if (token == null) return Validate(fallback);
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            throw new AdvisorConnectionException("request_timeout_invalid");
        return Validate(seconds);
    }
}
