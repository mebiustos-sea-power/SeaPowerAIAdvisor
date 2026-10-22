using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed partial class AdvisorQuerySession
{
    public JArray ExportSituation()
    {
        var export = new AdvisorSnapshotExport(Dispatch);
        void Details(string query, IEnumerable<string> ids) { if (information.CampaignQuery(query)) export.Details(query, ids); }
        void Pages(string query) { if (information.CampaignQuery(query)) export.Pages(query); }
        Details("get_forces", forces.Keys);
        Details("get_ships", ships.Keys);
        Details("get_air_units", Enumerable.Range(0, snapshot.Aviation.Units.Count).Select(SnapshotIds.AirUnit));
        Pages("list_air_flights"); Pages("list_contacts"); Pages("list_bases");
        Details("get_bases", snapshot.Ground.Bases().Select(b => (string)b["id"]!));
        Details("get_ground_units", snapshot.Ground.Units().Select(u => (string)u["id"]!));
        Details("get_base_observations", observations.Keys.Select(SnapshotIds.Base));
        Details("get_orders", Enumerable.Range(0, snapshot.Orders.Count).Select(SnapshotIds.Order));
        Details("get_reports", Enumerable.Range(0, snapshot.Reports.Count).Select(SnapshotIds.Report));
        return export.Data();
    }
}
