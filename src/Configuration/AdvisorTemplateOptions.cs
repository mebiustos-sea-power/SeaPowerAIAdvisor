using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal enum AdvisorTemplateInsertion { Replace, Append }

// Template presentation and insertion preferences never change consultation or connection policies.
internal sealed class AdvisorTemplateOptions
{
    internal bool ShowSystem { get; }
    internal AdvisorTemplateInsertion Insertion { get; }
    internal AdvisorTemplateOptions(bool showSystem = true, AdvisorTemplateInsertion insertion = AdvisorTemplateInsertion.Replace)
    {
        if (!Enum.IsDefined(typeof(AdvisorTemplateInsertion), insertion)) throw new AdvisorOptionsException("options_invalid");
        ShowSystem = showSystem; Insertion = insertion;
    }
    internal string Insert(string current, string template) => Insertion == AdvisorTemplateInsertion.Replace || current.Length == 0
        ? template : current + (current.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n") + template;
    internal JObject Json() => new() { ["show_system"] = ShowSystem, ["insertion"] = Insertion == AdvisorTemplateInsertion.Replace ? "replace" : "append" };
    internal static AdvisorTemplateOptions Parse(JToken? value)
    {
        if (value == null) return new AdvisorTemplateOptions();
        if (value is not JObject data || data.Properties().Any(p => p.Name is not ("show_system" or "insertion")) ||
            data["show_system"] != null && data["show_system"]!.Type != JTokenType.Boolean)
            throw new AdvisorOptionsException("options_invalid");
        var insertion = data["insertion"] == null ? AdvisorTemplateInsertion.Replace :
            data["insertion"]!.Type == JTokenType.String ? (string?)data["insertion"] switch
            {
                "replace" => AdvisorTemplateInsertion.Replace, "append" => AdvisorTemplateInsertion.Append,
                _ => throw new AdvisorOptionsException("options_invalid")
            } : throw new AdvisorOptionsException("options_invalid");
        return new AdvisorTemplateOptions((bool?)data["show_system"] ?? true, insertion);
    }
}
