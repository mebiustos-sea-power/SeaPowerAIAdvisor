using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Connection-level input policy; provider settings remain opaque to the host.
internal enum AdvisorSituationDelivery { OnDemand, Upfront, OnRequest }

internal static class AdvisorSituationDeliverySetting
{
    internal static string Code(this AdvisorSituationDelivery value) => value switch
    {
        AdvisorSituationDelivery.OnDemand => "on_demand", AdvisorSituationDelivery.Upfront => "upfront", AdvisorSituationDelivery.OnRequest => "on_request",
        _ => throw new AdvisorConnectionException("settings_invalid")
    };
    internal static AdvisorSituationDelivery Read(JObject source) => source["situation_delivery"] == null
        ? AdvisorSituationDelivery.OnDemand
        : source["situation_delivery"]!.Type != JTokenType.String ? throw new AdvisorConnectionException("settings_invalid")
        : (string?)source["situation_delivery"] switch
        {
            "on_demand" => AdvisorSituationDelivery.OnDemand, "upfront" => AdvisorSituationDelivery.Upfront, "on_request" => AdvisorSituationDelivery.OnRequest,
            _ => throw new AdvisorConnectionException("settings_invalid")
        };
}
