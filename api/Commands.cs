using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SeaPowerAIAdvisor.Api;

// Register once after host activation. Execution instances belong to one loaded battle.
public interface IAdvisorOperationExtension
{
    int ApiVersion { get; }
    OperationDefinition GetDefinition();
    IAdvisorOperationHandler CreateSession(IAdvisorOperationContext context);
}

public sealed class OperationDefinition
{
    public string Id { get; }
    public string Description { get; }
    public string ParametersJson { get; }
    public bool RequiresApproval { get; }
    public OperationPlanPolicy PlanPolicy { get; }
    public IReadOnlyDictionary<string, string> Localizations { get; }
    public OperationDefinition(string id, string description, string parametersJson,
        IReadOnlyDictionary<string, string> localizations, bool requiresApproval = true, OperationPlanPolicy? planPolicy = null)
    {
        Id = id; Description = description; ParametersJson = parametersJson; RequiresApproval = requiresApproval;
        PlanPolicy = planPolicy ?? new OperationPlanPolicy(1, 1, 1, OperationPlanSelection.Single);
        Localizations = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(
            localizations ?? throw new ArgumentNullException(nameof(localizations))));
    }
}

public enum OperationPlanSelection { Single, Multiple }

public sealed class OperationPlanPolicy
{
    public int Target { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public OperationPlanSelection Selection { get; }
    public OperationPlanPolicy(int target, int minimum, int maximum, OperationPlanSelection selection)
    {
        if (minimum < 1 || maximum < minimum || target < minimum || target > maximum ||
            !Enum.IsDefined(typeof(OperationPlanSelection), selection)) throw new ArgumentOutOfRangeException(nameof(minimum));
        Target = target; Minimum = minimum; Maximum = maximum; Selection = selection;
    }
}

// Arguments and results are JSON text; no particular JSON, UI or game library is required.
public sealed class OperationOrder
{
    public string Target { get; }
    public string ArgumentsJson { get; }
    public OperationOrder(string target, string argumentsJson) { Target = target; ArgumentsJson = argumentsJson; }
}

public interface IAdvisorOperationHandler : IDisposable
{
    string GetCandidates();
    void Validate(IReadOnlyList<OperationOrder> orders);
    string DescribeProposal(IReadOnlyList<OperationOrder> orders, string language);
    string Execute(OperationOrder order);
}

public interface IAdvisorOperationContext
{
    IAdvisorGameSupport Game { get; }
    IAdvisorMap Map { get; }
    IAdvisorOperationText Text { get; }
}

public interface IAdvisorOperationText
{
    string Get(string language, string key);
    string Format(string language, string key, params object[] arguments);
}

// Optional instructions can follow a command's live prompt source. Other definition fields are frozen at registration.
public interface IAdvisorOperationInstructions { string GetInstructions(); }
public interface IAdvisorOperationPreview
{
    AdvisorMapPreview Preview(IReadOnlyList<OperationOrder> orders, string language);
}
public interface IAdvisorOperationArgumentNormalizer
{
    OperationArgumentNormalization NormalizeArguments(OperationOrder order, IAdvisorOperationReferences references);
}

public interface IAdvisorOperationReferences
{
    string? ResolveUnit(string consultationId);
    OperationReferenceMatch MatchAmmunition(string requested, IReadOnlyList<AmmunitionIdentity> candidates);
}

public sealed class AmmunitionIdentity
{
    public string Id { get; }
    public string Name { get; }
    public AmmunitionIdentity(string id, string name) { Id = id; Name = name; }
}
public sealed class OperationReferenceMatch
{
    public IReadOnlyList<string> Ids { get; }
    public string Reason { get; }
    public OperationReferenceMatch(IEnumerable<string> ids, string reason)
    { Ids = new List<string>(ids).AsReadOnly(); Reason = reason; }
}
public sealed class OperationArgumentResolution
{
    public string Argument { get; }
    public string Requested { get; }
    public string Resolved { get; }
    public string Reason { get; }
    public OperationArgumentResolution(string argument, string requested, string resolved, string reason)
    { Argument = argument; Requested = requested; Resolved = resolved; Reason = reason; }
}
public sealed class OperationArgumentNormalization
{
    public string ArgumentsJson { get; }
    public IReadOnlyList<OperationArgumentResolution> Resolutions { get; }
    public OperationArgumentNormalization(string argumentsJson, IEnumerable<OperationArgumentResolution>? resolutions = null)
    { ArgumentsJson = argumentsJson; Resolutions = new List<OperationArgumentResolution>(resolutions ?? Array.Empty<OperationArgumentResolution>()).AsReadOnly(); }
}

// Expected command rejection. Unexpected exceptions remain visible through the host's fault handling.
public sealed class OperationException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public OperationException(int status, string code) : base(code)
    {
        if (status < 400 || status > 599) throw new ArgumentOutOfRangeException(nameof(status));
        if (string.IsNullOrEmpty(code) || code.Length > 80) throw new ArgumentException("Use a fixed error code.", nameof(code));
        foreach (var c in code)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) throw new ArgumentException("Use a fixed lowercase error code.", nameof(code));
        Status = status; Code = code;
    }
}
