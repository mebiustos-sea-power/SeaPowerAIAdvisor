using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using SeaPowerAIAdvisor.Shared;
using SeaPowerAIMissionCreator.Connections;

namespace SeaPowerAIMissionCreator.Development;

internal sealed class MissionCreatorLocalApiDriver : MonoBehaviour
{
    private MissionCreatorConnections? connections;
    private MissionCreatorLocalApi? api;
    private LocalApiServer? server;
    private Action<string>? warn;
    private bool dirty;
    private string? boundEndpoint;
    internal string Status { get; private set; } = "";
    internal event Action? Tick;
    private static string DiscoveryPath => Path.Combine(Path.GetTempPath(), "SeaPowerAIMissionCreator", "developer-api.json");
    internal void Initialize(MissionCreatorConnections connections, Action<string> warn)
    { this.connections = connections; this.warn = warn; api = new MissionCreatorLocalApi(connections); connections.Store.Changed += Changed; dirty = true; RemoveDiscovery(); }
    private void Changed() => dirty = true;
    private void Update()
    {
        if (connections == null || api == null) return;
        if (dirty)
        {
            dirty = false;
            var enabled = connections.Store.Current.DeveloperApiEnabled;
            var endpoint = "http://127.0.0.1:" + connections.Store.Current.DeveloperApiPort;
            if (!enabled || server == null || boundEndpoint != endpoint)
            {
                server?.Dispose(); server = null; boundEndpoint = null; RemoveDiscovery(); Status = "";
            }
            if (enabled && server == null)
            {
                try
                {
                    server = new LocalApiServer(api.Dispatch, message => warn?.Invoke(message), endpoint, "X-MissionCreator-Developer"); server.Start(); boundEndpoint = endpoint;
                    var info = new JObject { ["endpoint"] = endpoint, ["process_id"] = Process.GetCurrentProcess().Id, ["feature"] = "mission_creator" };
                    ConfigFile.Write(DiscoveryPath, Encoding.UTF8.GetBytes(info.ToString(Formatting.Indented) + "\n")); Status = endpoint + "\n" + DiscoveryPath;
                }
                catch (Exception error) { server?.Dispose(); server = null; RemoveDiscovery(); Status = error.Message; warn?.Invoke("Mission Creator developer API could not start: " + error); }
            }
        }
        if (server?.Failure is string failure) { server.Dispose(); server = null; RemoveDiscovery(); Status = failure; }
        Tick?.Invoke();
    }
    private void RemoveDiscovery()
    {
        try { if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { warn?.Invoke("Mission Creator developer discovery cleanup failed: " + error); }
    }
    private void Stop()
    { if (connections != null) { connections.Store.Changed -= Changed; connections.Dispose(); } server?.Dispose(); server = null; connections = null; RemoveDiscovery(); Tick = null; }
    private void OnApplicationQuit() => Stop();
    private void OnDestroy() => Stop();
}
