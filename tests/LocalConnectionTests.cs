using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class LocalApiTests
{
    private static void RunConnections(Action<string, Action> check)
    {
        check("Local connection selection enforces the saved allowlist and default without changing normal assignments", () =>
        {
            var settings = LocalLibrary();
            var policy = new AdvisorLocalConnections(new[] { "local", "sub" }, "local");
            var target = new Target(); var selected = "";
            var api = new AdvisorLocalApi(() => new AdvisorPromptOverrides(AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory)), error => throw error,
                () => policy.Describe(settings), (id, fallback, model) =>
                {
                    selected = id;
                    var draft = LocalLibrary(); policy.Select(draft, id, fallback, model);
                    return AdvisorServices.Prepare(draft, allowFallback: fallback);
                });
            api.Attach(new object(), "tactical_battle", () => target); api.Enable();
            var metadata = Send(api, "GET", "/v1/connections");
            Require(metadata.Status == 200 && (string?)metadata.Data["default_connection_id"] == "local" &&
                metadata.Data["connections"]!.Select(c => (string?)c["id"]).SequenceEqual(new[] { "local", "sub" }) &&
                !metadata.Data.ToString().Contains("mock-key", StringComparison.Ordinal), "Connection discovery exposed an unapproved connection or credentials");
            foreach (var id in new[] { "normal", "deleted" })
                Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Hello", ["connection_id"] = id }).Status == 403 &&
                    !target.Called && selected == "", "A forbidden connection was prepared or executed");
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Hello", ["connection_id"] = 12 }).Status == 400 &&
                !target.Called, "A malformed connection ID reached execution");
            var accepted = Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Hello" });
            Require(accepted.Status == 202 && selected == "local" && (string?)accepted.Data["connection_id"] == "local" &&
                (string?)accepted.Data["connection_name"] == "Local experiment" && !target.Run!.AllowFallback && settings.MainId == "normal", "Default selection used the normal main, enabled fallback, or changed assignments");
            Send(api, "POST", "/v1/consultations/cancel");
            accepted = Send(api, "POST", "/v1/consultations", new JObject { ["shortcut"] = "key_risks", ["connection_id"] = "sub" });
            Require(accepted.Status == 202 && selected == "sub", "An allowed explicit choice did not override the developer default");
            Send(api, "POST", "/v1/consultations/cancel");
            policy = AdvisorLocalConnections.Empty;
            Require(Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Hello" }).Data["error"]!.ToString() == "default_connection_missing",
                "An unset default silently selected a normal connection");
            policy = new AdvisorLocalConnections(new[] { "deleted" }, "deleted");
            var deletedDefault = Send(api, "GET", "/v1/connections").Data;
            var rejectedDefault = Send(api, "POST", "/v1/consultations", new JObject { ["question"] = "Hello" });
            Require((string?)deletedDefault["default_connection_id"] == null && rejectedDefault.Status == 409,
                "Deleting the default caused automatic reassignment: " + deletedDefault + " / " + rejectedDefault.Data);
            api.Disable();
        });
        check("Developer model overrides require permission and do not survive the consultation draft", () =>
        {
            var settings = LocalLibrary(); var original = settings.Data();
            var policy = new AdvisorLocalConnections(new[] { "local", "sub" }, "local");
            var target = new Target();
            var api = new AdvisorLocalApi(() => new AdvisorPromptOverrides(AdvisorPrompts.Load(AdvisorPrompts.DefaultDirectory)), error => throw error,
                () => policy.Describe(settings), (id, fallback, model) =>
                {
                    var draft = new AdvisorSettingsDraft(JObject.Parse(System.Text.Encoding.UTF8.GetString(original)));
                    policy.Select(draft, id, fallback, model);
                    return AdvisorServices.Prepare(draft, allowFallback: fallback);
                });
            api.Attach(new object(), "tactical_battle", () => target); api.Enable();
            var body = new JObject { ["question"] = "Hello", ["model"] = "gemini-experiment" };
            var denied = Send(api, "POST", "/v1/consultations", body);
            Require(denied.Status == 403 && (string?)denied.Data["error"] == "model_override_not_allowed" && !target.Called,
                "A model override ran without saved permission");
            policy = new AdvisorLocalConnections(new[] { "local", "sub" }, "local", true);
            foreach (var model in new JToken[] { JValue.CreateNull(), new JValue(false), new JValue(1), new JObject(),
                new JValue(""), new JValue("   "), new JValue("gemini-\ninvalid"), new JValue(new string('x', 201)) })
            {
                body["model"] = model;
                var invalid = Send(api, "POST", "/v1/consultations", body);
                Require(invalid.Status == 400 && (string?)invalid.Data["error"] == "invalid_model" && !target.Called,
                    "An invalid model reached consultation execution");
            }
            body["model"] = "gemini-experiment"; body["connection_id"] = "normal";
            Require(Send(api, "POST", "/v1/consultations", body).Status == 403 && !target.Called,
                "Model override permission bypassed the connection allowlist");
            body.Remove("connection_id");
            var accepted = Send(api, "POST", "/v1/consultations", body);
            Require(accepted.Status == 202 && (string?)accepted.Data["model"] == "gemini-experiment" &&
                (string?)accepted.Data["connection_name"] == "Local experiment" && !target.Run!.AllowFallback,
                "The accepted model or existing fallback policy changed");
            Send(api, "POST", "/v1/consultations/cancel");
            accepted = Send(api, "POST", "/v1/consultations", new JObject { ["shortcut"] = "key_risks" });
            Require(accepted.Status == 202 && (string?)accepted.Data["model"] == "gemini-test-model" &&
                settings.Data().SequenceEqual(original), "The override leaked into the next consultation or registered settings");
            api.Disable();
        });
        check("Developer choices persist independently and the Temp discovery file follows the effective permitted metadata", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "advisor-local-discovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new AdvisorOptionsStore(Path.Combine(directory, "options.json"));
                Require(!store.Current.LocalConnections.AllowModelOverride && !AdvisorOptions.Parse("{}").LocalConnections.AllowModelOverride &&
                    !AdvisorLocalConnections.Parse(JObject.Parse("{\"allowed_ids\":[],\"default_id\":null}")).AllowModelOverride &&
                    store.Current.LocalConnections.AllowedIds.Count == 0 && store.Current.LocalConnections.DefaultId == null,
                    "An existing installation implicitly permitted connections");
                var policy = new AdvisorLocalConnections(new[] { "local" }, "local", true);
                store.SaveDeveloper(true, () => { }, policy);
                store.EnsureUserName("Commander"); store.SaveGeneral(AdvisorTextSize.Small, false, "Commander");
                store.SaveConsultation(4, 2, ""); store.SaveInformation(AdvisorInformation.Default); store.SaveWip(true);
                var reopened = new AdvisorOptionsStore(store.Path);
                Require(reopened.Current.LocalConnections.AllowModelOverride && reopened.Current.LocalConnections.DefaultId == "local" && reopened.Current.LocalConnections.AllowedIds.SequenceEqual(new[] { "local" }),
                    "Another category or restart lost permitted/default connection choices");
                var before = File.ReadAllBytes(store.Path);
                try { reopened.SaveDeveloper(false, () => throw new IOException("blocked"), AdvisorLocalConnections.Empty); }
                catch (IOException) { }
                Require(reopened.Current.DeveloperApiEnabled && reopened.Current.LocalConnections.AllowModelOverride && reopened.Current.LocalConnections.DefaultId == "local" &&
                    File.ReadAllBytes(store.Path).SequenceEqual(before), "A failed developer save partly published or persisted connection permissions");
                var discovery = new AdvisorLocalDiscovery(Path.Combine(directory, "developer-api.json"), 123);
                var metadata = policy.Describe(LocalLibrary()); discovery.Write(metadata);
                var file = JObject.Parse(File.ReadAllText(discovery.FilePath));
                Require((bool?)file["allow_model_override"] == true && (int?)file["process_id"] == 123 && (string?)file["endpoint"] == AdvisorLocalServer.Endpoint &&
                    JToken.DeepEquals(file["connections"], metadata["connections"]) && (string?)file["default_connection_id"] == "local" &&
                    !File.ReadAllText(discovery.FilePath).Contains("mock-key", StringComparison.Ordinal), "Temp discovery disagreed with allowed API metadata or exposed credentials");
                discovery.Write(AdvisorLocalConnections.Empty.Describe(LocalLibrary()));
                file = JObject.Parse(File.ReadAllText(discovery.FilePath));
                Require((bool?)file["allow_model_override"] == false && !file["connections"]!.Any() && file["default_connection_id"]!.Type == JTokenType.Null,
                    "A permission change left previous connections in the discovery file");
                discovery.Remove(); discovery.Remove(); Require(!File.Exists(discovery.FilePath), "Disable/exit cleanup retained the discovery file");
                foreach (var json in new[] { "null", "[]", "{\"allowed_ids\":[true],\"default_id\":null}",
                    "{\"allowed_ids\":[\"x\",\"x\"],\"default_id\":null}", "{\"allowed_ids\":[],\"default_id\":\"x\"}", "{\"allowed_ids\":[],\"default_id\":null,\"allow_model_override\":null}",
                    "{\"allowed_ids\":[],\"default_id\":null,\"allow_model_override\":\"true\"}" })
                {
                    try { _ = AdvisorOptions.Parse("{\"developer_local_api_connections\":" + json + "}"); }
                    catch (AdvisorOptionsException) { continue; }
                    throw new InvalidOperationException("Malformed developer connection permissions were accepted");
                }
            }
            finally { Directory.Delete(directory, true); }
        });
    }
    private static AdvisorSettingsDraft LocalLibrary()
    {
        var draft = new AdvisorSettingsDraft(new JObject());
        foreach (var (id, name) in new[] { ("normal", "Normal main"), ("local", "Local experiment"), ("sub", "Optional sub") })
            draft.Put(new AdvisorApiConfigurationDraft(new AdvisorApiConfiguration(id, name, "mock-key-" + id, "gemini-test-model")));
        draft.MainId = "normal"; draft.SubId = "sub";
        return draft;
    }
}
