using System;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class Program
{
    private static int count;

    private static void Main(string[] args)
    {
        Check("Japanese multiline questions and XML punctuation survive unchanged", () =>
        {
            var chat = new PrototypeConversation();
            var question = "佐世保へ向かうべきですか？\n燃料・護衛の優先順位も知りたい。\n<>&\" 日本語𠮷";
            Require(chat.TrySend(question, out _), "Valid question rejected");
            Require(chat.Exchanges.Single().Question == question, "User text was modified");
            Require(chat.Exchanges.Single().Answer.Contains(question, StringComparison.Ordinal), "The displayed echo lost characters");
        });
        Check("Blank questions do not create a response or destroy previous conversation", () =>
        {
            var chat = new PrototypeConversation(); chat.TrySend("偵察を優先すべき？", out _);
            foreach (var empty in new string?[] { null, "", " \t\n", "　" })
                Require(!chat.TrySend(empty, out var error) && error.Length > 0 && chat.Exchanges.Count == 1, "Blank input changed the conversation");
        });
        Check("Question size limit rejects oversized input without changing history", () =>
        {
            var chat = new PrototypeConversation();
            Require(chat.TrySend(new string('あ', PrototypeConversation.MaxQuestionLength), out _), "Boundary question rejected");
            Require(!chat.TrySend(new string('あ', PrototypeConversation.MaxQuestionLength + 1), out _), "Oversized question accepted");
            Require(chat.Exchanges.Count == 1, "Rejected question was recorded");
        });
        Check("Follow-up questions preserve ordered exchanges", () =>
        {
            var chat = new PrototypeConversation();
            chat.TrySend("補給について", out _); chat.TrySend("その場合、護衛は？", out _);
            Require(chat.Exchanges.Count == 2 && chat.Exchanges[0].Question == "補給について" && chat.Exchanges[1].Question == "その場合、護衛は？", "Follow-up history was corrupted");
        });
        Check("Long conversations retain the latest exchanges", () =>
        {
            var chat = new PrototypeConversation();
            for (var i = 0; i <= PrototypeConversation.MaxExchanges; i++) chat.TrySend("質問" + i, out _);
            Require(chat.Exchanges.Count == PrototypeConversation.MaxExchanges && chat.Exchanges[0].Question == "質問1", "History retention is incorrect");
        });
        Check("Clearing conversation permits a fresh first question", () =>
        {
            var chat = new PrototypeConversation(); chat.TrySend("以前の会話", out _); chat.Clear(); chat.TrySend("新しい会話", out _);
            Require(chat.Exchanges.Count == 1 && chat.Exchanges[0].Question == "新しい会話", "Previous conversation survived clearing");
        });
        Check("Installed campaign has the lifecycle hooks and mode contract used by the adapter", () =>
        {
            using var original = AssemblyDefinition.ReadAssembly(args[0]);
            Method(original, "DynamicCampaignMod.DeferredPatcher", "Patch", true, "System.Void", 0);
            Method(original, "DynamicCampaignMod.UI.CampaignUI", "Frame", false, "System.Void", 0);
            Method(original, "DynamicCampaignMod.UI.CampaignUI", "Detach", false, "System.Void", 0);
            Method(original, "DynamicCampaignMod.Game.Session.CampaignSession", "OnSceneUnloaded", true, "System.Void", 1);
            var session = original.MainModule.GetType("DynamicCampaignMod.Game.Session.CampaignSession");
            Require(session.Properties.Any(p => p.Name == "Active" && p.PropertyType.FullName == "System.Boolean" && p.GetMethod.IsStatic), "Active contract changed");
            Require(session.Properties.Any(p => p.Name == "Sim" && p.GetMethod.IsStatic), "Sim contract changed");
            Require(session.Fields.Any(f => f.Name == "CampaignSceneIndex" && f.HasConstant && f.Constant is int), "Scene contract changed");
            Console.WriteLine("Campaign assembly: " + original.Name.Version);
        });
        Check("Installed strategy toolbar supports an advisor entry in its own layout", () =>
        {
            using var original = AssemblyDefinition.ReadAssembly(args[0]);
            var ui = original.MainModule.GetType("DynamicCampaignMod.UI.CampaignUI");
            Require(ui.Fields.Any(f => f.Name == "_strategyView" && f.FieldType.FullName == "Noesis.FrameworkElement"), "Strategy view contract changed");
            var resource = original.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "StrategyBar.xaml");
            using var stream = resource.GetResourceStream();
            var root = XDocument.Load(stream).Root!;
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            Require(root.Name == ns + "Grid" && root.Elements(ns + "Border").Count(b =>
                (string?)b.Attribute("Grid.Column") == "0" && b.Element(ns + "StackPanel") != null) == 1,
                "No unique toolbar container to participate in layout");
            Require(root.Element(ns + "Grid.Resources")!.Elements(ns + "Style").Any(e =>
                (string?)e.Attribute(x + "Key") == "StrategyButton" && ((string?)e.Attribute("TargetType"))!.Contains("ToggleButton")),
                "Toolbar button styling contract changed");
        });
        Check("Built prototype carries its embedded view and has no AI networking dependency", () =>
        {
            using var built = AssemblyDefinition.ReadAssembly(args[1]);
            Require(built.MainModule.Resources.Any(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml"), "Embedded window is missing");
            Require(!built.MainModule.GetTypeReferences().Any(t => t.Namespace.StartsWith("System.Net", StringComparison.Ordinal)), "Unexpected networking code in the offline prototype");
        });
        SituationTests.Run(args[0], Check, args.Skip(2).ToArray());
        Console.WriteLine("PASS " + count + " checks");
    }

    private static void Method(AssemblyDefinition assembly, string type, string name, bool isStatic, string returns, int arguments)
    {
        var matches = assembly.MainModule.GetType(type)?.Methods.Where(m => m.Name == name).ToArray() ?? Array.Empty<MethodDefinition>();
        Require(matches.Length == 1 && matches[0].IsStatic == isStatic && matches[0].ReturnType.FullName == returns && matches[0].Parameters.Count == arguments,
            "Hook signature changed: " + type + "." + name);
    }

    private static void Check(string name, Action test)
    {
        test(); count++; Console.WriteLine("PASS " + name);
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
