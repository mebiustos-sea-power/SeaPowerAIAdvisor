using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static class BattleTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Battle capture excludes hidden objectives, future reports and actual enemy details", () =>
        {
            var f = new Fixture();
            var before = f.Capture().Copy().ToString();
            f.Secret.Value = "Changed actual enemy position, armament and orders";
            f.Hidden.Secret = "Changed hidden victory rule";
            f.Future.Secret = "Changed future intelligence";
            Require(f.Capture().Copy().ToString() == before, "Undisclosed information influenced the snapshot");
            var data = f.Capture().Copy();
            Require(((JArray)data["public_objectives"]!).Count == 1 && ((JArray)data["published_reports"]!).Count == 1, "Publication filters were bypassed");
            Require((string?)data["friendly_units"]![0]!["display_name"] == "ミッドウェイ" &&
                (string?)data["observed_contacts"]![0]!["display_name"] == "画面で識別した艦", "Displayed names were lost");
        });
        check("Unknown contact position stays unknown and a captured battle snapshot remains detached", () =>
        {
            var f = new Fixture();
            f.Track.PositionEstimate = null;
            f.Track.Class = null;
            var captured = f.Capture();
            var data = captured.Copy();
            Require(data["observed_contacts"]![0]!["position"]!.Type == JTokenType.Null &&
                (string?)data["observed_contacts"]![0]!["position_source"] == "unknown", "Missing position was invented");
            Require((string?)data["observed_contacts"]![0]!["display_name"] == "Contact 7", "An unidentified name was disclosed");
            f.Own.Name.Value = "Renamed after capture";
            data["friendly_units"]![0]!["display_name"] = "Mutated consumer copy";
            Require((string?)captured.Copy()["friendly_units"]![0]!["display_name"] == "ミッドウェイ", "Snapshot retained mutable sources or consumer aliases");
            try { _ = f.Capture(true); throw new InvalidOperationException("Enemy-control disclosure was accepted"); }
            catch (SituationUnavailableException e) { Require(e.Code == "battle_disclosure", "Wrong refusal reason"); }
        });
        check("Battle queries support current IDs and pages while rejecting foreign IDs and bounded invalid calls", () =>
        {
            var session = new BattleQuerySession(new Fixture().Capture(), "fr", "次の行動は？");
            var first = session.InitialInput();
            Require((string?)first["mode"] == "tactical_battle" && (string?)first["reply_language"] == "fr" &&
                (string?)first["situation"]!["victory_conditions"]!["objectives"]![0]!["status"] == "pending", "Mandatory mission context is missing");
            var response = session.Execute(Request(session, new JObject { ["query"] = "list_units", ["limit"] = 1 }));
            var id = response["results"]![0]!["data"]!["items"]![0]!["id"]!.DeepClone();
            response = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray(id) }));
            Require((double?)response["results"]![0]!["data"]![0]!["speed_knots"] == 18, "Known own-unit detail was lost");
            response = session.Execute(Request(session, new JObject { ["query"] = "get_units", ["ids"] = new JArray("enemy_internal_id") }));
            Require((string?)response["error"]!["code"] == "unknown_id", "An undisclosed ID was accepted");
            var invalid = Request(session, new JObject { ["query"] = "list_contacts", ["limit"] = 21 });
            Require((string?)session.Execute(invalid)["error"]!["code"] == "invalid_arguments", "Oversized page accepted");
            session.Execute(new JObject { ["consultation_id"] = "previous-consultation", ["requests"] = new JArray(new JObject { ["query"] = "catalog" }) });
            Require((string?)session.Execute(Request(session, new JObject { ["query"] = "catalog" }))["error"]!["code"] == "round_limit", "Invalid calls bypassed the round budget");
        });
        check("Gemini uses battle-specific tools through the common provider", () =>
        {
            var session = new BattleQuerySession(new Fixture().Capture(), "ja", "まず何をすべき？");
            var transport = new MockBattleTransport();
            var settings = AdvisorSettings.Parse("{\"api_key\":\"mock-local-key\",\"model\":\"gemini-3.5-flash-lite\",\"log_ai_json\":false}");
            var result = new GeminiAdvisor(transport).ConsultAsync(session, settings, Array.Empty<ChatExchange>(), CancellationToken.None).GetAwaiter().GetResult();
            Require(result.ApiRequests == 3 && result.QueryRounds == 2 && result.Queries.Contains("get_units") &&
                result.Text == "公開目標を優先し、接触を確認してください。", "Battle consultation did not complete");
        });
    }

    private static JObject Request(IAdvisorQuerySession session, params JObject[] queries) =>
        new() { ["consultation_id"] = session.Id, ["requests"] = new JArray(queries) };

    private sealed class MockBattleTransport : IGeminiTransport
    {
        private int calls;
        public Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
        {
            calls++;
            var input = JObject.Parse((string)body["contents"]![0]!["parts"]![0]!["text"]!);
            Require((string?)input["mode"] == "tactical_battle" && (string?)input["reply_language"] == "ja", "Provider changed mode or reply language");
            JObject part;
            if (calls == 1) part = Call(new JObject { ["query"] = "list_units" }, new JObject { ["query"] = "list_contacts" }, new JObject { ["query"] = "get_briefing" });
            else if (calls == 2)
            {
                var answer = body["contents"]![2]!["parts"]![0]!["functionResponse"]!["response"]!;
                var id = answer["results"]![0]!["data"]!["items"]![0]!["id"]!.DeepClone();
                Require((string?)answer["results"]![2]!["data"]!["text"] == "公開済みの任務説明", "Briefing was not returned");
                part = Call(new JObject { ["query"] = "get_units", ["ids"] = new JArray(id) });
            }
            else part = new JObject { ["text"] = "公開目標を優先し、接触を確認してください。" };
            return Task.FromResult(new JObject
            {
                ["candidates"] = new JArray(new JObject { ["finishReason"] = "STOP", ["content"] = new JObject { ["role"] = "model", ["parts"] = new JArray(part) } }),
                ["usageMetadata"] = new JObject { ["totalTokenCount"] = 100 }
            });
        }
        private static JObject Call(params JObject[] requests) => new()
        {
            ["functionCall"] = new JObject { ["name"] = "query_situation", ["args"] = new JObject { ["requests"] = new JArray(requests) } }
        };
    }

    private sealed class Fixture
    {
        internal readonly OwnUnit Own = new();
        internal readonly SecretEnemy Secret = new();
        internal readonly Track Track;
        internal readonly HiddenObjective Hidden = new();
        internal readonly FutureReport Future = new();
        private readonly DateTime now = new(1985, 6, 1, 12, 0, 0);
        internal Fixture() { Track = new Track { Object = Secret }; }
        internal BattleSnapshot Capture(bool disclosure = false) => new BattleSituationReader().Capture(
            new object[]
            {
                new { Unit = new { Object = Own } },
                new { Unit = Track, ObjectViewModel = new { ObjectName = new Reactive<string>("画面で識別した艦") }, VisualType = "Surface", VisualPosture = "Unknown" }
            }, new { TaskforceObjects = new[] { Own } },
            new object[] { new { IsHidden = false, Text = "輸送船を保護する", Status = 0 }, Hidden },
            new
            {
                MissionBriefing = new Reactive<string>("公開済みの任務説明"),
                Intelligence = new object[] { new { Time = now.AddMinutes(-1), IntelligenceText = "接触の報告" }, Future },
                AllIntelligence = new Forbidden()
            }, now, "Test scenario", disclosure);
    }
    private sealed class Reactive<T>
    {
        public T Value { get; set; }
        internal Reactive(T value) { Value = value; }
    }
    private sealed class OwnUnit
    {
        public Reactive<string> Name { get; } = new("ミッドウェイ");
        public string _type { get; } = "Vessel";
        public Reactive<Geo> Position { get; } = new(new Geo(31, 131));
        public Reactive<double> Heading { get; } = new(270);
        public double _velocityInKnots { get; } = 18;
    }
    private sealed class Geo
    {
        public double Latitude { get; }
        public double Longitude { get; }
        internal Geo(double latitude, double longitude) { Latitude = latitude; Longitude = longitude; }
    }
    private sealed class Track
    {
        public object Object { get; set; } = null!;
        public object? PositionEstimate { get; set; } = new { Item1 = new Geo(32, 130) };
        public object? Class { get; set; } = new object();
        public object Position => throw new InvalidOperationException("Read a contact position outside its estimate");
        public bool HasLiveTrack => false;
        public Reactive<bool> Identified { get; } = new(true);
        public Reactive<bool> IsDormant { get; } = new(true);
        public int Id => 7;
    }
    private sealed class SecretEnemy
    {
        internal string Value = "Actual enemy secrets";
        public object Position => throw new InvalidOperationException("Read actual enemy position");
        public string Name => throw new InvalidOperationException("Read actual enemy name");
    }
    private sealed class HiddenObjective
    {
        internal string Secret = "Secret win trigger";
        public bool IsHidden => true;
        public string Text => throw new InvalidOperationException("Read a hidden objective");
    }
    private sealed class FutureReport
    {
        internal string Secret = "Future scripted report";
        public DateTime Time => new(1985, 6, 2);
        public string IntelligenceText => throw new InvalidOperationException("Read future intelligence");
    }
    private sealed class Forbidden { }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
