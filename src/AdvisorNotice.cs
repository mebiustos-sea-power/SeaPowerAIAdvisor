namespace SeaPowerAIAdvisor;

// Provider details stay as fixed codes; presentation never displays raw JSON or credentials.
internal sealed class AdvisorNotice
{
    internal string TextKey { get; }
    internal string Code { get; }
    internal int? HttpStatus { get; }
    private readonly bool usingFallback;
    private AdvisorNotice(string key, string code, int? status = null, bool fallback = false)
    { TextKey = key; Code = code; HttpStatus = status; usingFallback = fallback; }

    internal static AdvisorNotice Connection(AdvisorConnectionException error)
    {
        var key = error.Code switch
        {
            "settings_missing" or "settings_unreadable" => "settings_missing",
            "api_key_missing" => "api_key_missing",
            "fallback_key_missing" => "fallback_key_missing",
            "model_invalid" => "model_invalid",
            "fallback_model_invalid" => "fallback_model_invalid",
            "settings_invalid" => "settings_invalid",
            "api_configuration_name_invalid" or "api_main_missing" or "api_selection_invalid" or "api_selection_same" => error.Code,
            "api_access_denied" => "api_access_denied",
            "api_bad_request" => "api_bad_request",
            "api_error" => "api_error",
            "model_unavailable" => "model_unavailable",
            "diagnostic_log_error" => "diagnostic_log_error",
            "rate_limited" => "rate_limited",
            "request_timeout" or "consultation_timeout" => "request_timeout",
            "network_error" => "network_error",
            "output_limit" => "output_limit",
            "response_blocked" or "generation_failed" => "response_blocked",
            "request_budget" or "request_limit" => "request_budget",
            _ => "unknown"
        };
        return new AdvisorNotice("connection." + key, error.Code, error.HttpStatus, error.UsingFallback);
    }

    internal static AdvisorNotice Options(AdvisorOptionsException error) => new("settings.error." + error.Code, error.Code);
    internal static AdvisorNotice Situation(string code) => new("situation.error." + code, code);
    internal static AdvisorNotice Prompts(AdvisorPromptException error) => new("prompts.error." + error.Code, error.Code);
    internal static AdvisorNotice Unexpected() => new("connection.unknown", "unexpected_error");
    internal string Message(AdvisorText text) => (usingFallback ? text["connection.fallback_failed"] + " " : "") + text[TextKey] + " [" + Code +
        (HttpStatus.HasValue ? " / HTTP " + HttpStatus : "") + "]";
}
