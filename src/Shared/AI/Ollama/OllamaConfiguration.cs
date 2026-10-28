using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal static class OllamaConfiguration
{
    internal static Uri Endpoint(JObject settings)
    {
        var value = (string?)settings["base_url"];
        if (value == null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new AiConnectionException("server_url_invalid");
        // Accept a reverse-proxy prefix, but never treat the supplied value as an arbitrary API endpoint.
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/api/chat");
    }
    internal static int ContextLength(JObject settings)
    {
        if (!int.TryParse((string?)settings["num_ctx"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 4096 || n > 262144)
            throw new AiConnectionException("context_length_invalid");
        return n;
    }
}
