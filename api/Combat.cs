using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Api;

// Native attack handling stays in the game adapter. Targets expose only the player's map knowledge.
public interface IAdvisorCombatSupport
{
    IReadOnlyList<GameUnitInfo> GetCombatUnits();
    IReadOnlyList<CombatTargetInfo> GetAttackTargets();
    IReadOnlyList<AttackUnitInfo> GetAttackUnits();
    void IssueAttack(string target, AttackUpdateMode mode, IReadOnlyList<AttackInstruction> attacks);
    void CeaseFire(string target);
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
    public AttackUnitInfo(GameUnitInfo unit, IEnumerable<AttackAmmunitionInfo> ammunition, IEnumerable<AttackInstruction> pendingAttacks)
    { Unit = unit; Ammunition = new List<AttackAmmunitionInfo>(ammunition).AsReadOnly();
        PendingAttacks = new List<AttackInstruction>(pendingAttacks).AsReadOnly(); }
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
