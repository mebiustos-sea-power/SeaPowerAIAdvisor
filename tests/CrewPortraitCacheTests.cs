using System;
using System.IO;
using System.Linq;
using Noesis;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;
using UnityEngine;

internal static class CrewPortraitCacheTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Portrait updates preserve the shared image source and release replaced textures without invalidating existing image bindings", () =>
        {
            var texture = new PortraitTexture(CrewTests.PortraitHeader(128, 256)); var bound = texture.Source; var previous = bound.Texture.Native;
            var updated = CrewTests.PortraitHeader(256, 128); texture.Update(updated);
            Require(ReferenceEquals(bound, texture.Source) && !bound.Disposed && bound.Texture.Native.Bytes.SequenceEqual(updated) && !bound.Texture.Native.Destroyed,
                "Updating disposed or replaced the image source still held by UI bindings");
            Require(previous.Destroyed && bound.Invalidations == 1, "The replaced texture leaked or the shared source was not invalidated");
            var current = bound.Texture.Native; texture.Dispose();
            Require(bound.Disposed && current.Destroyed, "Disposal did not release the current image resources");
        });
        check("A decoder failure releases its temporary texture while preserving the existing bound source and image", () =>
        {
            using var texture = new PortraitTexture(CrewTests.PortraitHeader(128, 256)); var source = texture.Source; var current = source.Texture.Native;
            ImageConversion.RejectNextLoad = true;
            Throws<PortraitDataException>(() => texture.Update(CrewTests.PortraitHeader(256, 128)));
            Require(ReferenceEquals(source, texture.Source) && ReferenceEquals(current, source.Texture.Native) && !source.Disposed && !current.Destroyed,
                "A rejected decode changed or destroyed the current image");
            Require(Texture2D.LastCreated!.Destroyed && !ReferenceEquals(Texture2D.LastCreated, current), "Rejected decoding leaked its temporary texture");
        });
        check("Portrait replacement updates every borrower of an image source once without redecoding unrelated images", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var original = CrewTests.PortraitHeader(128, 256); var next = CrewTests.PortraitHeader(256, 128);
            var id = library.Add(original); var other = library.Add(original);
            using var portraits = new CrewPortraits(library);
            var historyBinding = (TextureSource)portraits.GetImage(id); var personBinding = portraits.GetImage(id); var old = historyBinding.Texture.Native;
            var unrelated = (TextureSource)portraits.GetImage(other); var unrelatedTexture = unrelated.Texture.Native; var loads = ImageConversion.Loads;
            library.Replace(id, next); var current = portraits.GetImage(id);
            Require(ReferenceEquals(current, historyBinding) && ReferenceEquals(current, personBinding) && historyBinding.Texture.Native.Bytes.SequenceEqual(next) && old.Destroyed,
                "The replacement did not update a previously borrowed source or leaked the old texture");
            Require(ReferenceEquals(unrelated, portraits.GetImage(other)) && ReferenceEquals(unrelated.Texture.Native, unrelatedTexture) && !unrelatedTexture.Destroyed &&
                ReferenceEquals(current, portraits.GetImage(id)) && ImageConversion.Loads == loads + 1, "Replacement recreated an unrelated or already-current texture");
            library.Replace(id, original); portraits.GetImage(id);
            Require(historyBinding.Texture.Native.Bytes.SequenceEqual(original) && ImageConversion.Loads == loads + 2, "A second replacement was missed or decoded more than once");
            portraits.Retain(Array.Empty<string>());
            Require(historyBinding.Disposed && historyBinding.Texture.Native.Destroyed && unrelated.Disposed && unrelatedTexture.Destroyed, "Releasing unused image IDs leaked current resources");
        }));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected rejection: " + typeof(T).Name); }
    private static void Temp(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "crew-cache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, true); }
    }
}
