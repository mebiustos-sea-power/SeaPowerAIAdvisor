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
            var expected = new[] { Code.Ldc_I4_0, Code.Ldc_I4_1, Code.Ldc_I4_0, Code.Ldc_I4_0 };
            if (!code.Skip(index - 4).Take(4).Select(i => i.OpCode.Code).SequenceEqual(expected))
                throw new InvalidOperationException("Native cancellation can remove reservations, propagate to another actor, or leave offensive jamming running.");
        });
    }
}
