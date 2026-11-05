using System;
using Noesis;
using UnityEngine;

namespace SeaPowerAIAdvisor.Shared;

// Create and release on Unity's main thread, after detaching every UI image using Source.
internal sealed class PortraitTexture : IDisposable
{
    private readonly Texture2D texture;
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
    public void Dispose() { Source.Dispose(); UnityEngine.Object.Destroy(texture); }
}
