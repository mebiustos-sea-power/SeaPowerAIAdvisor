using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor.Api;

public sealed class ConnectionField
{
    public string Key { get; }
    public string Label { get; }
    public string DefaultValue { get; }
    public bool Secret { get; }
    public bool Numeric { get; }
    private readonly IReadOnlyDictionary<string, string> translations;
    public ConnectionField(string key, string label, string defaultValue = "", bool secret = false,
        IReadOnlyDictionary<string, string>? translations = null)
        : this(key, label, false, defaultValue, secret, translations) { }
    public ConnectionField(string key, string label, bool numeric, string defaultValue = "", bool secret = false,
        IReadOnlyDictionary<string, string>? translations = null)
    {
        Key = key; Label = label; DefaultValue = defaultValue; Secret = secret; Numeric = numeric;
        this.translations = new ReadOnlyDictionary<string, string>(translations == null
            ? new Dictionary<string, string>() : new Dictionary<string, string>(translations));
    }
    public string GetLabel(string uiLanguage) => translations.TryGetValue(uiLanguage, out var label) ? label : Label;
}

// Implement exactly one of IStandardAdvisorExtension and IIndependentAdvisorExtension.
// Settings are JSON text, so the contract does not depend on a JSON or game library.
public interface IAdvisorExtension
{
    int ApiVersion { get; }
    string Id { get; }
    string Name { get; }
    string Protocol { get; }
    IReadOnlyList<ConnectionField> Fields { get; }
    void ValidateSettings(string settingsJson);
    string GetModel(string settingsJson);
    Task<AdvisorAnswer> TestAsync(ConnectionSettings connection, IAdvisorConnectionServices services,
        string testMessage, CancellationToken cancellation);
}

// Optional display version of this individual command or adapter, independent of ApiVersion and MOD versions.
public interface IAdvisorExtensionVersion
{
    string? Version { get; }
}

// Optional adapter overview for the Extensions list, separate from connection setup guidance.
public interface IAdvisorExtensionSummary
{
    string GetSummary(string uiLanguage);
}

// Optional provider-wide guidance for the connection form. Empty text leaves no display area.
public interface IAdvisorExtensionDescription
{
    string GetDescription(string uiLanguage);
}

public interface IStandardAdvisorExtension : IAdvisorExtension
{
    IAdvisorSession OpenSession(ConnectionSettings connection, IAdvisorConnectionServices services,
        ConsultationInput input, RetrievalHistory? continuation);
}

// Opt into ConsultationInput.ToolsJson and expose every supplied definition unchanged.
// IStandardAdvisorExtension alone retains the original two-tool contract.
public interface IAdvisorToolListExtension : IStandardAdvisorExtension { }

public interface IIndependentAdvisorExtension : IAdvisorExtension
{
    IAdvisorConsultation OpenConsultation(ConnectionSettings connection, IAdvisorConnectionServices services);
}

public sealed class ConnectionSettings
{
    public string Id { get; }
    public string Name { get; }
    public string ProviderId { get; }
    public string SettingsJson { get; }
    public string Model { get; }
    public int RequestTimeoutSeconds { get; }
    public int MaxOutputTokens { get; }
    public bool IsSubConnection { get; }
    public ConnectionSettings(string id, string name, string providerId, string settingsJson, string model,
        int requestTimeoutSeconds, int maxOutputTokens, bool isSubConnection)
    { Id = id; Name = name; ProviderId = providerId; SettingsJson = settingsJson; Model = model;
        RequestTimeoutSeconds = requestTimeoutSeconds; MaxOutputTokens = maxOutputTokens; IsSubConnection = isSubConnection; }
}

// PostJsonAsync supplies timeout, cancellation, error classification and redacted logging.
// An extension using its own transport can report bodies through Begin/End/FailRequest.
// Never pass HTTP headers or credentials to those diagnostic methods.
public interface IAdvisorConnectionServices
{
    Task<string> PostJsonAsync(Uri endpoint, string bodyJson, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? headers = null);
    int BeginRequest(string bodyJson);
    void EndRequest(int request, int httpStatus, string body, bool complete = true);
    void FailRequest(int request, string errorCode, int? httpStatus = null);
}

// Fixed codes and optional HTTP statuses only; raw service text stays in diagnostics.
public sealed class AdvisorException : Exception
{
    public string Code { get; }
    public int? HttpStatus { get; }
    public AdvisorException(string code, int? httpStatus = null) : base(code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 80) throw new ArgumentException("Use a fixed error code.", nameof(code));
        foreach (var c in code)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                throw new ArgumentException("Use a fixed lowercase error code.", nameof(code));
        if (httpStatus.HasValue && (httpStatus < 100 || httpStatus > 599)) throw new ArgumentOutOfRangeException(nameof(httpStatus));
        Code = code; HttpStatus = httpStatus;
    }
}
