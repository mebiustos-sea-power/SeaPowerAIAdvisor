using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal sealed class LocalApiException : Exception
{
    internal int Status { get; }
    internal string Code { get; }
    internal LocalApiException(int status, string code) : base(code) { Status = status; Code = code; }
}

internal sealed class LocalApiResponse
{
    internal int Status { get; }
    internal JObject Data { get; }
    internal LocalApiResponse(int status, JObject data) { Status = status; Data = data; }
    internal static LocalApiResponse Error(int status, string code) => new(status, new JObject { ["error"] = code });
}
