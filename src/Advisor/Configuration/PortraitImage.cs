using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor;

// Profile callers keep their existing domain errors; image parsing is shared with Crew.
internal static class PortraitImage
{
    internal const int MaxBytes = PortraitData.MaxBytes;
    internal const int MaxDimension = PortraitData.MaxDimension;
    internal static bool SupportsPath(string path) => PortraitData.SupportsPath(path);
    internal static string Extension(byte[] bytes)
    {
        try { return PortraitData.Extension(bytes); }
        catch (PortraitDataException e) { throw new AdvisorProfileException(e.Code); }
    }
}
