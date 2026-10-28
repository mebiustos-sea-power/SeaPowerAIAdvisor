using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

// Bundled messages use the UI language and stay separate from the editable user library.
internal static class AdvisorSystemTemplates
{
    internal static IReadOnlyList<AdvisorTextTemplate> Read(AdvisorText text) => new[]
    {
        new AdvisorTextTemplate("system-weapon-employment", text["templates.weapon_employment.name"], text["templates.weapon_employment.message"])
    };
}
