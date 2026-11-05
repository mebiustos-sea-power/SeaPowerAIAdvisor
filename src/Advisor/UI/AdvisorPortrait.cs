using System;
using Noesis;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorPortrait : IDisposable
{
    private readonly PortraitTexture portrait;
    internal TextureSource Source => portrait.Source;
    internal AdvisorPortrait(byte[] image)
    {
        try { portrait = new PortraitTexture(image); }
        catch (PortraitDataException e) { throw new AdvisorProfileException(e.Code); }
    }
    public void Dispose() => portrait.Dispose();
}
