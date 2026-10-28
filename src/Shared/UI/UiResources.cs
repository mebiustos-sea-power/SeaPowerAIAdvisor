using System;
using System.IO;
using System.Text;
using Noesis;

namespace SeaPowerAIAdvisor.Shared;

// Shared control visuals stay scoped to each view; game brushes use Noesis resource lookup.
internal static class UiResources
{
    // Resolve on a loaded view; overrides stay local and retain the native checkbox template.
    internal static void ApplyDisabledCheckBoxBrushes(FrameworkElement scope)
    {
        scope.Resources["Brush.Background.Checked"] = (Brush)scope.FindResource("Brush.Background.Normal");
        scope.Resources["Brush.Border.DisabledChecked"] = (Brush)scope.FindResource("Brush.Border.Disabled");
        scope.Resources["Brush.Glyph.DisabledChecked"] = (Brush)scope.FindResource("Brush.Glyph.Disabled");
    }

    internal static void Apply(FrameworkElement view)
    {
        using var source = typeof(UiResources).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.AdvisorControls.xaml")
            ?? throw new FileNotFoundException("The embedded shared control resources are missing.");
        using var reader = new StreamReader(source, Encoding.UTF8);
        var resources = GUI.ParseXaml(reader.ReadToEnd()) as ResourceDictionary
            ?? throw new InvalidOperationException("The shared control resources are not a ResourceDictionary.");
        view.Resources.MergedDictionaries.Add(resources);
    }
}
