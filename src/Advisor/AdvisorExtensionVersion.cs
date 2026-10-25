using System;
using System.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Freeze optional display metadata at registration; never substitute an assembly or contract version.
internal static class AdvisorExtensionVersion
{
    internal static string? Read(object extension)
    {
        var value = (extension as IAdvisorExtensionVersion)?.Version;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 80 || value.Any(char.IsControl))
            throw new ArgumentException("Extension display version must be a single line of at most 80 characters.", nameof(extension));
        return value;
    }
}
