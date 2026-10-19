using System;
using UnityEngine;

namespace SeaPowerAIAdvisor;

// The driver exists only after this mod passes its enabled check. Its Update is the main-thread boundary.
internal sealed class AdvisorLocalApiDriver : MonoBehaviour
{
    private AdvisorOptionsStore? options;
    private AdvisorLocalApi? api;
    private AdvisorLocalServer? server;
    private Action<string>? warn;
    private bool attempted;
    internal static string Status { get; private set; } = "";
    internal void Initialize(AdvisorOptionsStore options, AdvisorLocalApi api, Action<string> warn)
    { this.options = options; this.api = api; this.warn = warn; }
    private void Update()
    {
        if (options == null || api == null) return;
        if (!options.Current.DeveloperApiEnabled)
        {
            if (attempted) { api.Disable(); server?.Dispose(); server = null; attempted = false; Status = ""; }
            return;
        }
        if (!attempted)
        {
            attempted = true;
            try { server = new AdvisorLocalServer(api.Enqueue, message => warn?.Invoke(message)); server.Start(); api.Enable(); Status = AdvisorLocalServer.Endpoint; }
            catch (Exception error) { server?.Dispose(); server = null; Status = error.Message; warn?.Invoke("Developer local API could not start: " + error); }
        }
        if (server?.Failure is string failure)
        {
            api.Disable(); server.Dispose(); server = null; Status = failure;
        }
        api.Pump();
    }
    private void OnApplicationQuit() => Stop();
    private void OnDestroy() => Stop();
    private void Stop() { api?.Disable(); server?.Dispose(); server = null; options = null; Status = ""; }
}
