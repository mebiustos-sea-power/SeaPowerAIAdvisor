using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Task = System.Threading.Tasks.Task;
using HarmonyLib;
using SeaPower;

namespace SeaPowerAIAdvisor.Shared;

// Explicitly installed once per MOD assembly. Shared knows neither features nor their data schema.
internal sealed class GameSaveIntegration : IDisposable
{
    private static GameSaveIntegration? active;
    [ThreadStatic] private static SaveWrite? writing;
    private readonly Harmony patches;
    private readonly SaveDataSection section;
    private readonly SaveDataSession session;
    private readonly Action<string> error;

    internal GameSaveIntegration(string owner, Action reset, Func<Func<byte[]?>?> prepareCapture, Action<byte[]?> restore, Action<string> error)
    {
        if (active != null) throw new InvalidOperationException("This MOD already has a save integration.");
        section = new SaveDataSection(owner); session = new SaveDataSession(reset, prepareCapture, restore); this.error = error;
        patches = new Harmony(owner + ".save-data"); active = this;
        try
        {
            patches.Patch(Required(typeof(SaveLoadManager), "SaveGame"),
                prefix: Hook(nameof(Saving)), finalizer: Hook(nameof(SaveFinished)));
            patches.Patch(Required(typeof(SaveLoadManager), "WriteMissionToFile"), transpiler: Hook(nameof(InsertSaveData)));
            patches.Patch(Required(typeof(SceneCreator), "LoadMission"), prefix: Hook(nameof(Loading)));
            patches.Patch(Required(typeof(SaveLoadManager), "LoadMetadata"), postfix: Hook(nameof(MetadataLoaded)));
        }
        catch { Dispose(); throw; }
    }
    internal void Tick()
    {
        if (!SceneCreator.InstanceExists(false)) return;
        try { session.Complete(SceneCreator.Instance, SceneCreator.Instance.IsLoadingDone); }
        catch (Exception failure) { error("MOD save-data restoration failed: " + failure); }
    }
    private static void Loading(SceneCreator __instance)
    {
        try { active!.session.Begin(__instance); }
        catch (Exception failure) { active!.error("MOD save-data reset failed: " + failure); }
    }
    private static void MetadataLoaded()
    {
        var current = active!;
        try
        {
            var scene = SceneCreator.Instance;
            current.session.Stage(scene, SaveLoadManager.IsASaveFile ? current.section.Read(scene.MissionIni.Data) : null);
        }
        catch (Exception failure) { current.error("MOD save-data loading failed: " + failure); }
    }
    private static bool Saving(SaveLoadManager.SaveType __1, bool ____saveLoadInProgress, ref string __result, out SaveWrite? __state)
    {
        __state = writing;
        if (__1 != SaveLoadManager.SaveType.InGame || ____saveLoadInProgress) return true;
        var current = active!;
        try
        {
            current.Tick();
            // Capture and encode before the native save sets its busy flag or starts writing files.
            writing = new SaveWrite(current.session.Capture());
            return true;
        }
        catch (Exception failure)
        {
            current.error("Game save was not started because MOD data could not be captured: " + failure);
            __result = ""; return false;
        }
    }
    // A finalizer restores nested synchronous save scopes even if native code throws. It does not suppress that exception.
    private static void SaveFinished(SaveWrite? __state) => writing = __state;
    private static void Write(IniHandler ini, SaveLoadManager.SaveType type)
    {
        if (type != SaveLoadManager.SaveType.InGame) return;
        var snapshot = writing ?? throw new InvalidOperationException("The native save bypassed the MOD capture boundary.");
        active!.section.Write(ini.Data, snapshot.Payload);
    }
    private static IEnumerable<CodeInstruction> InsertSaveData(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var create = Required(typeof(IniHandler), "create");
        var schedule = typeof(Task).GetMethod(nameof(Task.Run), new[] { typeof(Func<Task>) })!;
        var creates = code.Select((op, i) => (op, i)).Where(x => x.op.Calls(create)).Select(x => x.i).ToArray();
        var starts = code.Select((op, i) => (op, i)).Where(x => x.op.Calls(schedule)).Select(x => x.i).ToArray();
        if (creates.Length != 1 || starts.Length != 1) throw Unsupported();
        var start = starts[0] - 3;
        if (creates[0] + 1 >= code.Count || code[creates[0] + 1].opcode != OpCodes.Stfld ||
            code[creates[0] + 1].operand is not FieldInfo ini || ini.FieldType != typeof(IniHandler) || start < 0 ||
            code[start + 1].opcode != OpCodes.Ldftn || code[start + 1].operand is not MethodInfo factory ||
            factory.DeclaringType != ini.DeclaringType || code[start + 2].opcode != OpCodes.Newobj ||
            !LoadsLocal(code[start]) || code[start].blocks.Count != 0) throw Unsupported();
        // Use the native save's own INI, after synchronous serialization and before Task.Run can observe it.
        var receiver = new CodeInstruction(code[start].opcode, code[start].operand);
        code[start].MoveLabelsTo(receiver);
        code.InsertRange(start, new[] { receiver, new CodeInstruction(OpCodes.Ldfld, ini),
            new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, Required(typeof(GameSaveIntegration), nameof(Write))) });
        return code;
    }
    private static bool LoadsLocal(CodeInstruction op) => op.opcode == OpCodes.Ldloc || op.opcode == OpCodes.Ldloc_S ||
        op.opcode == OpCodes.Ldloc_0 || op.opcode == OpCodes.Ldloc_1 || op.opcode == OpCodes.Ldloc_2 || op.opcode == OpCodes.Ldloc_3;
    private static InvalidOperationException Unsupported() => new("The native save scheduling boundary has changed; MOD save integration was not installed.");
    private static MethodInfo Required(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
    private static HarmonyMethod Hook(string name) => new(Required(typeof(GameSaveIntegration), name));
    public void Dispose()
    {
        patches.UnpatchSelf();
        if (ReferenceEquals(active, this)) active = null;
    }
    private sealed class SaveWrite
    {
        internal string? Payload { get; }
        internal SaveWrite(string? payload) { Payload = payload; }
    }
}
