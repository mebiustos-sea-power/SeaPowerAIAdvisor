using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

// Native attack handling stays in the game adapter. Targets expose only the player's map knowledge.
public interface IAdvisorCombatSupport
{
    IReadOnlyList<GameUnitInfo> GetCombatUnits();
    IReadOnlyList<CombatTargetInfo> GetAttackTargets();
    IReadOnlyList<AttackUnitInfo> GetAttackUnits();
    IReadOnlyList<AttackUnitInfo> GetInRangeAttackUnits(IReadOnlyList<string> contacts, string? shooter = null);
    void IssueAttack(string target, AttackUpdateMode mode, IReadOnlyList<AttackInstruction> attacks);
    void CancelAttack(string target);
}

// Optional contact-scoped candidate lookup; ordinary discovery remains available for approach planning.
public interface IAdvisorOperationContactCandidates
{
    string GetCandidates(IReadOnlyList<string> contacts, string? shooter);
}

public enum AttackUpdateMode { Append, Replace }

public sealed class CombatTargetInfo
{
    public string Target { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Posture { get; }
    public AdvisorMapCoordinate? Position { get; }
    public bool Dormant { get; }
    public bool Destroyed { get; }
    public CombatTargetInfo(string target, string name, string kind, string posture, AdvisorMapCoordinate? position, bool dormant, bool destroyed)
    { Target = target; Name = name; Kind = kind; Posture = posture; Position = position; Dormant = dormant; Destroyed = destroyed; }
}

public sealed class AttackUnitInfo
{
    public GameUnitInfo Unit { get; }
    public IReadOnlyList<AttackAmmunitionInfo> Ammunition { get; }
    public IReadOnlyList<AttackInstruction> PendingAttacks { get; }
    public IReadOnlyList<AttackRangeInfo> Ranges { get; }
    public AttackUnitInfo(GameUnitInfo unit, IEnumerable<AttackAmmunitionInfo> ammunition, IEnumerable<AttackInstruction> pendingAttacks,
        IEnumerable<AttackRangeInfo>? ranges = null)
    { Unit = unit; Ammunition = new List<AttackAmmunitionInfo>(ammunition).AsReadOnly();
        PendingAttacks = new List<AttackInstruction>(pendingAttacks).AsReadOnly();
        Ranges = new List<AttackRangeInfo>(ranges ?? Array.Empty<AttackRangeInfo>()).AsReadOnly(); }
}

// Native range estimates, in nautical miles. Reloading and automatic approach remain game-owned.
public sealed class AttackRangeInfo
{
    public string Target { get; }
    public string Ammunition { get; }
    public double Distance { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public string LimitingFactor { get; }
    public bool InRange => Distance >= Minimum && Distance <= Maximum;
    public AttackRangeInfo(string target, string ammunition, double distance, double minimum, double maximum, string limitingFactor)
    {
        if (double.IsNaN(distance) || double.IsInfinity(distance) || distance < 0 ||
            double.IsNaN(minimum) || double.IsInfinity(minimum) || minimum < 0 ||
            double.IsNaN(maximum) || double.IsInfinity(maximum) || maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "Range values must be finite and nonnegative.");
        Target = target; Ammunition = ammunition; Distance = distance; Minimum = minimum; Maximum = maximum; LimitingFactor = limitingFactor;
    }
}

public sealed class AttackAmmunitionInfo
{
    public string Id { get; }
    public string Name { get; }
    public int Remaining { get; }
    public int Available { get; }
    public int AvailableAfterSwitch { get; }
    public IReadOnlyList<string> CompatibleTargets { get; }
    public AttackAmmunitionInfo(string id, string name, int remaining, int available, int availableAfterSwitch, IEnumerable<string> compatibleTargets)
    { Id = id; Name = name; Remaining = remaining; Available = available; AvailableAfterSwitch = availableAfterSwitch;
        CompatibleTargets = new List<string>(compatibleTargets).AsReadOnly(); }
}

public sealed class AttackInstruction
{
    public string Target { get; }
    public string Ammunition { get; }
    public int Rounds { get; }
    public AttackInstruction(string target, string ammunition, int rounds)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(ammunition) || rounds < 1)
            throw new ArgumentException("An attack requires a target, ammunition and a positive shot count.");
        Target = target; Ammunition = ammunition; Rounds = rounds;
    }
}
