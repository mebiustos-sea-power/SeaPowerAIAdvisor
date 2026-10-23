using System;
using System.IO;
using System.Text;
using Noesis;

namespace SeaPowerAIAdvisor;

// Shared control visuals stay scoped to each view; game brushes use Noesis resource lookup.
internal static class AdvisorUiResources
{
    internal static void Apply(FrameworkElement view)
    {
        using var source = typeof(AdvisorUiResources).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.AdvisorControls.xaml")
            ?? throw new FileNotFoundException("The embedded advisor control resources are missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var resources = GUI.ParseXaml(reader.ReadToEnd()) as ResourceDictionary
            ?? throw new InvalidOperationException("The advisor control resources are not a ResourceDictionary.");
        view.Resources.MergedDictionaries.Add(resources);
    }
}
