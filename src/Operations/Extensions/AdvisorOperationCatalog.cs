using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Registration owns immutable metadata; a handler is created lazily only for live command work.
internal sealed class AdvisorOperationCatalog
{
    internal static AdvisorOperationCatalog BuiltIn { get; } = CreateBuiltIn();
    private readonly object sync = new();
    private readonly List<AdvisorOperationRegistration> registrations = new();
    private static AdvisorOperationCatalog CreateBuiltIn()
    { var catalog = new AdvisorOperationCatalog(); catalog.Register(new SonobuoyDropExtension()); catalog.Register(new MovementExtension()); catalog.Register(new FormationReturnExtension()); return catalog; }
    internal string Register(IAdvisorOperationExtension extension)
    {
        if (extension == null) throw new ArgumentNullException(nameof(extension));
        if (extension.ApiVersion != AdvisorApi.ContractVersion) throw new NotSupportedException("Unsupported AI Advisor operation contract version.");
        var registration = new AdvisorOperationRegistration(extension);
        lock (sync)
        {
            if (registrations.Any(r => r.Definition.Id == registration.Definition.Id))
                throw new InvalidOperationException("Duplicate operation ID: " + registration.Definition.Id);
            registrations.Add(registration);
            return registration.Definition.Id;
        }
    }
    internal IReadOnlyList<AdvisorRegisteredOperation> CreateOperations(IAdvisorGameSupport game, IAdvisorMap map)
    {
        lock (sync) return registrations.Select(r => new AdvisorRegisteredOperation(r, game, map)).ToArray();
    }
}

internal sealed class AdvisorOperationRegistration
{
    internal IAdvisorOperationExtension Extension { get; }
    internal OperationDefinition Definition { get; }
    internal AdvisorOperationText Text { get; }
    internal JObject Parameters { get; }
    internal AdvisorOperationPlanPolicy Policy { get; }
    internal AdvisorOperationRegistration(IAdvisorOperationExtension extension)
    {
        Extension = extension;
        Definition = extension.GetDefinition() ?? throw new InvalidOperationException("The command definition is missing.");
        if (string.IsNullOrWhiteSpace(Definition.Id) || Definition.Id.Length > 80 || !Definition.Id.Contains('.') ||
            Definition.Id.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')) ||
            string.IsNullOrWhiteSpace(Definition.Description)) throw new ArgumentException("Invalid operation definition.", nameof(extension));
        Parameters = JObject.Parse(Definition.ParametersJson);
        if ((string?)Parameters["type"] != "object") throw new ArgumentException("Command arguments must have an object schema.", nameof(extension));
        Text = new AdvisorOperationText(Definition.Localizations);
        var policy = Definition.PlanPolicy;
        Policy = new AdvisorOperationPlanPolicy(policy.Target, policy.Minimum, policy.Maximum,
            policy.Selection == OperationPlanSelection.Single ? AdvisorPlanSelection.Single : AdvisorPlanSelection.Multiple);
    }
    internal string Instructions
    {
        get
        {
            var text = Extension is IAdvisorOperationInstructions live ? live.GetInstructions() : Definition.Description;
            return !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidOperationException("Command instructions are empty.");
        }
    }
}
