using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Connections;

namespace SeaPowerAIMissionCreator.Development;

// These endpoints only inspect detached configuration and run network tests; they never touch game/UI objects.
internal sealed class MissionCreatorLocalApi
{
    private readonly MissionCreatorConnections connections;
    internal MissionCreatorLocalApi(MissionCreatorConnections connections) { this.connections = connections; }
    internal Task<LocalApiResponse> Dispatch(string method, string path, JObject body)
    {
        try
        {
            if (method == "GET" && path == "/status") return Reply(200, new JObject { ["feature"] = "mission_creator", ["test"] = connections.Last?.Status() });
            if (method == "GET" && path == "/connections") return Reply(200, connections.ConnectionInfo());
            if (method == "POST" && path == "/test")
            {
                if (body.Properties().Any(p => p.Name != "connection_id") || body["connection_id"] != null && body["connection_id"]!.Type != JTokenType.String)
                    return Reply(400, new JObject { ["error"] = "invalid_request" });
                var run = connections.StartTest((string?)body["connection_id"]); return Reply(202, run.Status());
            }
            if (method == "POST" && path == "/test/cancel")
            {
                if (body.Count != 0) return Reply(400, new JObject { ["error"] = "invalid_request" });
                connections.CancelTest(); return Reply(200, new JObject { ["test"] = connections.Last?.Status() });
            }
            return Reply(404, new JObject { ["error"] = "not_found" });
        }
        catch (AiConnectionException error) { return Reply(error.Code == "connection_test_in_progress" ? 409 : 400, new JObject { ["error"] = error.Code }); }
    }
    private static Task<LocalApiResponse> Reply(int status, JObject value) => Task.FromResult(new LocalApiResponse(status, value));
}
