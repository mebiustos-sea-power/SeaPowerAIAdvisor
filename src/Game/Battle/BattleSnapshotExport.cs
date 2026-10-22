using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed partial class BattleQuerySession
{
    public JArray ExportSituation()
    {
        var export = new AdvisorSnapshotExport(Dispatch);
        void Details(string query, string collection)
        {
            if (information.BattleQuery(query)) export.Details(query, Rows(collection).Select(r => (string)r["id"]!));
        }
        void Pages(string query) { if (information.BattleQuery(query)) export.Pages(query); }
        Details("get_units", "friendly_units");
        Details("get_unit_capabilities", "unit_capabilities");
        Details("get_movement_plans", "movement_plans");
        Details("get_bases", "friendly_bases");
        Details("get_air_units", "hosted_air_units");
        Pages("list_air_tasks"); Pages("list_launched_weapons"); Pages("list_contacts");
        Details("get_contact_threats", "contact_threat_references");
        Details("get_reports", "published_reports");
        if (assessment != null) export.Pages(BattleContactAssessment.Query);
        if (information.BattleQuery("get_briefing"))
        {
            if ((bool?)snapshot["mission_briefing_unavailable"] == true) export.Unavailable("get_briefing", "briefing_unavailable");
            else export.Single("get_briefing");
        }
        return export.Data();
    }
}
