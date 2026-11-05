using UnityEngine;

namespace SeaPowerAIAdvisor.Shared;

// Import-time processing on Unity's main thread. The caller owns the size policy and file storage.
internal static class PortraitResize
{
    internal static byte[] Fit(byte[] bytes, int maximum)
    {
        var image = PortraitData.Inspect(bytes);
        var size = PortraitData.FitSize(image.Width, image.Height, maximum);
        if (size.Width == image.Width && size.Height == image.Height) return bytes;

        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D? resized = null;
        RenderTexture? target = null;
        var previousTarget = RenderTexture.active;
        var previousSrgb = GL.sRGBWrite;
        try
        {
            if (!ImageConversion.LoadImage(source, bytes, true)) throw new PortraitDataException("portrait_invalid");
            source.filterMode = FilterMode.Bilinear; source.wrapMode = TextureWrapMode.Clamp;
            target = RenderTexture.GetTemporary(size.Width, size.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(source, target);
            RenderTexture.active = target;
            resized = new Texture2D(size.Width, size.Height, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, size.Width, size.Height), 0, 0, false);
            return ImageConversion.EncodeToPNG(resized);
        }
        finally
        {
            RenderTexture.active = previousTarget; GL.sRGBWrite = previousSrgb;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (resized != null) Object.Destroy(resized);
            Object.Destroy(source);
        }
    }
}
