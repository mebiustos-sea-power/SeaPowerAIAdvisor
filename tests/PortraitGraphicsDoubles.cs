using System;

// CPU-only stand-ins for the external Unity/Noesis APIs. These tests exercise ownership, not native rendering.
namespace UnityEngine
{
    internal class Object
    {
        internal bool Destroyed { get; private set; }
        public static void Destroy(Object value) => value.Destroyed = true;
    }
    internal class Texture : Object { }
    internal enum TextureFormat { RGBA32 }
    internal enum FilterMode { Bilinear }
    internal enum TextureWrapMode { Clamp }
    internal sealed class Texture2D : Texture
    {
        internal static Texture2D? LastCreated;
        internal byte[] Bytes = Array.Empty<byte>();
        public string name = "";
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public Texture2D(int width, int height, TextureFormat format, bool mipmap) => LastCreated = this;
    }
    internal static class ImageConversion
    {
        internal static bool RejectNextLoad;
        internal static int Loads;
        public static bool LoadImage(Texture2D texture, byte[] bytes, bool markNonReadable)
        {
            Loads++;
            if (RejectNextLoad) { RejectNextLoad = false; return false; }
            texture.Bytes = (byte[])bytes.Clone(); return true;
        }
    }
}
namespace Noesis
{
    internal abstract class ImageSource { }
    internal sealed class Texture
    {
        internal UnityEngine.Texture2D Native { get; }
        internal Texture(UnityEngine.Texture2D texture) => Native = texture;
    }
    internal sealed class TextureSource : ImageSource
    {
        public Texture Texture { get; set; }
        internal bool Disposed { get; private set; }
        internal int Invalidations { get; private set; }
        public TextureSource(UnityEngine.Texture texture) => Texture = new Texture((UnityEngine.Texture2D)texture);
        public void Invalidate() { if (Disposed) throw new ObjectDisposedException(nameof(TextureSource)); Invalidations++; }
        public void Dispose() => Disposed = true;
    }
}
