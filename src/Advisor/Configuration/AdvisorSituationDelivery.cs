using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Standard input policy; provider settings remain opaque to the host.
internal enum AdvisorSituationDelivery { OnDemand, Upfront, OnRequest }

internal static class AdvisorSituationDeliverySetting
{
    internal static string Code(this AdvisorSituationDelivery value) => value switch
    {
        AdvisorSituationDelivery.OnDemand => "on_demand", AdvisorSituationDelivery.Upfront => "upfront", AdvisorSituationDelivery.OnRequest => "on_request",
        _ => throw new AiConnectionException("settings_invalid")
    };
    internal static AdvisorSituationDelivery Read(JObject source) => source["situation_delivery"] == null
        ? AdvisorSituationDelivery.OnDemand
        : source["situation_delivery"]!.Type != JTokenType.String ? throw new AiConnectionException("settings_invalid")
        : Parse((string?)source["situation_delivery"]);
    internal static AdvisorSituationDelivery Parse(string? code) => code switch
        {
            "on_demand" => AdvisorSituationDelivery.OnDemand, "upfront" => AdvisorSituationDelivery.Upfront, "on_request" => AdvisorSituationDelivery.OnRequest,
            _ => throw new AiConnectionException("settings_invalid")
        };
}
