using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class CancellationBoundaryTests
{
    internal static void Run(string pluginPath, Action<string, Action> check)
    {
        check("Attack cancellation retains waypoint reservations and does not propagate through native formation flags", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(pluginPath);
            var method = assembly.MainModule.GetType("SeaPowerAIAdvisor.BattleOperationSupport").Methods.Single(m => m.Name == "CancelAttack");
            var code = method.Body.Instructions;
            var call = code.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "SeaPower.ObjectBase" && m.Name == "CeaseFire");
            var index = code.IndexOf(call);
            // The last four native arguments are formationCheck, stopJamming, airFormationCheck, clearAttackTasks.
            if (code.Any(i => i.Operand is MethodReference m && m.Name == "SetWeaponStatus"))
                throw new InvalidOperationException("Attack cancellation must not change the engagement policy.");
            var expected = new[] { Code.Ldc_I4_0, Code.Ldc_I4_1, Code.Ldc_I4_0, Code.Ldc_I4_0 };
            if (!code.Skip(index - 4).Take(4).Select(i => i.OpCode.Code).SequenceEqual(expected))
                throw new InvalidOperationException("Native cancellation can remove reservations, propagate to another actor, or leave offensive jamming running.");
        });
        check("Engagement policy changes use the native setter without formation propagation or attack/reservation cancellation", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(pluginPath);
            var method = assembly.MainModule.GetType("SeaPowerAIAdvisor.BattleOperationSupport").Methods.Single(m => m.Name == "SetRulesOfEngagement");
            var code = method.Body.Instructions;
            var call = code.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "SeaPower.ObjectBase" && m.Name == "SetWeaponStatus");
            if (code[code.IndexOf(call) - 1].OpCode.Code != Code.Ldc_I4_0 || code.Any(i => i.Operand is MethodReference m &&
                new[] { "CeaseFire", "CancelAttack", "ClearAttackTasks", "ClearReservations", "RemoveWaypoints" }.Contains(m.Name)))
                throw new InvalidOperationException("Engagement policy changes can propagate or cancel existing attacks/reservations.");
        });
    }
}
