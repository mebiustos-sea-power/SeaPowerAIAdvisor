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
        if (args.Length == 1 && args[0] == "--battle-only")
        {
            BattleTests.Run(Check);
            InformationTests.Run(Check);
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "DynamicCampaignMod"), "Battle-only verification loaded DCM");
            Console.WriteLine("PASS " + count + " battle checks (no DCM loaded)");
            return;
        }
        if (args.Length == 5 && args[0] == "--query-demo") { QueryDemo.Run(args[1], args[2], args[3], args[4]); return; }
        if (args.Length == 4 && args[0] == "--gemini-air-smoke")
        {
            try { GeminiAirSmoke.Run(args[1], args[2], args[3]); }
            catch (AdvisorConnectionException e) { Console.Error.WriteLine("Aviation evaluation failed: " + e.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 6 && args[0] == "--gemini-smoke")
        {
            try { GeminiSmoke.Run(args[1], args[2], args[3], args[4], args[5]); }
            catch (AdvisorConnectionException e) { Console.Error.WriteLine("Gemini smoke failed: " + e.Message); Environment.ExitCode = 1; }
            return;
        }
        Check("Japanese multiline questions and XML punctuation survive unchanged", () =>
        {
            var chat = new AdvisorConversation();
            var question = "佐世保へ向かうべきですか？\n燃料・護衛の優先順位も知りたい。\n<>&\" 日本語𠮷";
            Require(chat.TryAdd(question, "回答：" + question, out _), "Valid question rejected");
            Require(chat.Exchanges.Single().Question == question, "User text was modified");
            Require(chat.Exchanges.Single().Answer.Contains(question, StringComparison.Ordinal), "The stored answer lost characters");
        });
        Check("Blank questions do not create a response or destroy previous conversation", () =>
        {
            var chat = new AdvisorConversation(); chat.TryAdd("偵察を優先すべき？", "回答", out _);
            foreach (var empty in new string?[] { null, "", " \t\n", "　" })
                Require(!chat.TryAdd(empty, "回答", out var error) && error.Length > 0 && chat.Exchanges.Count == 1, "Blank input changed the conversation");
        });
        Check("Question size limit rejects oversized input without changing history", () =>
        {
            var chat = new AdvisorConversation();
            Require(chat.TryAdd(new string('あ', AdvisorConversation.MaxQuestionLength), "回答", out _), "Boundary question rejected");
            Require(!chat.TryAdd(new string('あ', AdvisorConversation.MaxQuestionLength + 1), "回答", out _), "Oversized question accepted");
            Require(chat.Exchanges.Count == 1, "Rejected question was recorded");
        });
        Check("Follow-up questions preserve ordered exchanges", () =>
        {
            var chat = new AdvisorConversation();
            chat.TryAdd("補給について", "回答", out _); chat.TryAdd("その場合、護衛は？", "回答", out _);
            Require(chat.Exchanges.Count == 2 && chat.Exchanges[0].Question == "補給について" && chat.Exchanges[1].Question == "その場合、護衛は？", "Follow-up history was corrupted");
        });
        Check("Long conversations retain the latest exchanges", () =>
        {
            var chat = new AdvisorConversation();
            for (var i = 0; i <= AdvisorConversation.MaxExchanges; i++) chat.TryAdd("質問" + i, "回答", out _);
            Require(chat.Exchanges.Count == AdvisorConversation.MaxExchanges && chat.Exchanges[0].Question == "質問1", "History retention is incorrect");
        });
        Check("Clearing conversation permits a fresh first question", () =>
        {
            var chat = new AdvisorConversation(); chat.TryAdd("以前の会話", "回答", out _); chat.Clear(); chat.TryAdd("新しい会話", "回答", out _);
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
        Check("Built advisor embeds its view and keeps networking out of the knowledge boundary", () =>
        {
            using var built = AssemblyDefinition.ReadAssembly(args[1]);
            Require(built.MainModule.Resources.Any(r => r.Name == "SeaPowerAIAdvisor.Advisor.xaml"), "Embedded window is missing");
            foreach (var type in new[] { "CampaignSituationReader", "CampaignAirReader", "CampaignVictoryReader", "AdvisorQuerySession", "AdvisorInputBuilder" })
                Require(!built.MainModule.GetType("SeaPowerAIAdvisor." + type).Methods.Where(m => m.HasBody)
                    .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MemberReference>()
                    .Any(m => (m.DeclaringType?.Namespace ?? (m as TypeReference)?.Namespace ?? "").StartsWith("System.Net", StringComparison.Ordinal)),
                    "Network access crossed the player-knowledge boundary");
        });
        BattleTests.Run(Check);
        SituationTests.Run(args[0], Check, args.Skip(3).ToArray());
        InputTests.Run(args[0], args[2], Check);
        QueryTests.Run(args[0], Check);
        AirTests.Run(args[0], Check);
        GeminiTests.Run(args[0], Check);
        ProfileTests.Run(args[1], Check);
        PreferencesTests.Run(Check);
        OptionsTests.Run(args[0], Check);
        InformationTests.Run(Check);
        InformationTests.RunCampaign(args[0], Check);
        ConversationTests.Run(Check);
        ConnectionTests.Run(Check);
        QuestionInputTests.Run(args[2], Check);
        PresentationTests.Run(args[1], args[0], Check);
        MarkdownTests.Run(args[1], Check);
        TraceTests.Run(args[0], Check);
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
