using System;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor.Shared;

// Create, update and release on Unity's main thread. Detach UI images before disposing Source.
internal sealed class PortraitTexture : IDisposable
{
    private Texture2D texture;
    internal TextureSource Source { get; }
    internal PortraitTexture(byte[] image)
    {
        PortraitData.Extension(image);
        texture = new Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, image, true)) throw new PortraitDataException("portrait_invalid");
            texture.name = "MOD portrait"; texture.filterMode = FilterMode.Bilinear; texture.wrapMode = TextureWrapMode.Clamp;
            Source = new TextureSource(texture);
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }
    internal void Update(byte[] image)
    {
        var replacement = new PortraitTexture(image);
        try
        {
            Source.Texture = replacement.Source.Texture;
            // Keep the bound Source; transfer texture ownership so disposal releases the previous texture.
            var previous = texture; texture = replacement.texture; replacement.texture = previous;
            Source.Invalidate();
        }
        finally { replacement.Dispose(); }
    }
    public void Dispose() { Source.Dispose(); UnityEngine.Object.Destroy(texture); }
}
