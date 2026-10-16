using System;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor;

// Construct, replace and dispose only on Unity's main thread. Every reload creates a fresh texture.
internal sealed class AdvisorPortrait : IDisposable
{
    private readonly Texture2D texture;
    internal TextureSource Source { get; }
    internal AdvisorPortrait(byte[] png)
    {
        texture = new Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, png, true)) throw new AdvisorProfileException("portrait_invalid");
            texture.name = "SeaPowerAIAdvisor portrait";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            Source = new TextureSource(texture);
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }
    public void Dispose()
    {
        Source.Dispose();
        UnityEngine.Object.Destroy(texture);
    }
}
