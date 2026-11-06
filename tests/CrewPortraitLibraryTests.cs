using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class CrewPortraitLibraryTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("A portrait library starts empty without writing and its image IDs survive reopening independently of source files", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory);
            Require(library.Ids().Count == 0 && !Directory.EnumerateFileSystemEntries(directory).Any(), "Reading an empty library created persistent data");
            var image = CrewTests.PortraitHeader(128, 256); var source = Path.Combine(directory, "source.png"); File.WriteAllBytes(source, image);
            var first = library.Add(image); var second = library.Add(image);
            Require(first != second && !Path.IsPathRooted(first) && !first.Contains('/') && !first.Contains('\\'), "Image identities are not independent library IDs");
            File.Delete(source);
            var reopened = new CrewPortraitLibrary(directory);
            Require(reopened.Ids().Count == 2 && reopened.Read(first)!.SequenceEqual(image) && reopened.Read(second)!.SequenceEqual(image), "Library images depended on the source or the old library instance");
        }));
        check("Portrait library storage keeps prepared PNG and JPEG bytes and rejects oversized input before publishing an image", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var png = CrewTests.PortraitHeader(256, 128);
            var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0, 128, 1, 0, 1, 1, 0x11, 0 };
            var first = library.Add(png); var second = library.Add(jpeg);
            Require(library.Read(first)!.SequenceEqual(png) && library.Read(second)!.SequenceEqual(jpeg), "Prepared image bytes were recompressed or mislabeled");
            var before = library.Version; var large = CrewTests.PortraitHeader(1200, 800);
            Require(PortraitData.Extension(large) == ".png", "Crew policy changed the common input limit");
            Throws<PortraitDataException>(() => library.Add(large));
            Require(library.Version == before && library.Ids().Count == 2 && library.Contains(first) && library.Contains(second), "Invalid input published an image or affected existing assets");
        }));
        check("Only explicit library deletion removes an image while source and other image files remain intact", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var bytes = CrewTests.PortraitHeader(256, 256);
            var first = library.Add(bytes); var second = library.Add(bytes);
            var source = Path.Combine(directory, first); File.WriteAllBytes(source, bytes);
            library.Delete(first);
            Require(library.Read(first) == null && library.Contains(second) && File.ReadAllBytes(source).SequenceEqual(bytes), "Deletion affected another asset or its source");
            Require(!new CrewPortraitLibrary(directory).Ids().Contains(first), "An explicitly deleted image returned after reopening");
        }));
        check("Portrait IDs cannot read or delete source paths, external images or the discarded per-person path format", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory); var image = CrewTests.PortraitHeader(256, 256);
            var source = Path.Combine(directory, "11111111111111111111111111111111.png"); File.WriteAllBytes(source, image);
            foreach (var invalid in new[] { source, "../" + Path.GetFileName(source), "Crew/portraits/portrait-11111111111111111111111111111111.png", "", "legacy.png" })
            {
                Throws<CrewPortraitLibraryException>(() => library.Read(invalid));
                Throws<CrewPortraitLibraryException>(() => library.Delete(invalid));
                Throws<CrewPortraitLibraryException>(() => new CrewPerson("Person", "", "", invalid));
            }
            Require(File.ReadAllBytes(source).SequenceEqual(image) && library.Ids().Count == 0, "An invalid image reference escaped the library");
        }));
        check("Missing portrait IDs use absence but corrupt images and blocked storage remain visible failures", () => Temp(directory =>
        {
            var library = new CrewPortraitLibrary(directory);
            Require(library.Read("11111111111111111111111111111111.png") == null, "A missing image was treated as corrupt data");
            var id = library.Add(CrewTests.PortraitHeader(256, 256));
            File.WriteAllText(Path.Combine(directory, "Crew", "PortraitLibrary", id), "invalid image");
            Throws<PortraitDataException>(() => library.Read(id));
            Require(library.Ids().Contains(id), "A corrupt image was silently removed from the library");
            File.Delete(Path.Combine(directory, "Crew", "PortraitLibrary", id)); Directory.Delete(Path.Combine(directory, "Crew", "PortraitLibrary"));
            File.WriteAllText(Path.Combine(directory, "Crew", "PortraitLibrary"), "blocked");
            var read = Throws<CrewPortraitLibraryException>(() => library.Ids());
            var write = Throws<CrewPortraitLibraryException>(() => library.Add(CrewTests.PortraitHeader(128, 128)));
            Require(read.Code == "portrait_library_read_failed" && write.Code == "portrait_library_write_failed" && library.Version == 1, "Storage failures were hidden or marked as successful writes");
        }));
    }
    internal static void RunViews(string advisorPath, Action<string, Action> check)
    {
        check("Crew library views resolve their own and host-supplied UI strings in both embedded localizations", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(advisorPath);
            string Read(string name)
            {
                var resource = assembly.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == name);
                using var stream = resource.GetResourceStream(); using var reader = new StreamReader(stream); return reader.ReadToEnd();
            }
            var views = new[] { "SeaPowerAIAdvisor.CrewPortraitLibrary.xaml", "SeaPowerAIAdvisor.CrewCharacterLibrary.xaml" }.Select(name => XDocument.Parse(Read(name))).ToArray();
            foreach (var language in new[] { "en", "ja" })
            {
                var text = JObject.Parse(Read("SeaPowerAIAdvisor.Crew.Localization." + language + ".json"));
                // The host supplies shared form actions from the Advisor's localization resources.
                var host = JObject.Parse(Read("SeaPowerAIAdvisor.Localization." + language + ".json"));
                text["save"] = host["settings.save_form"]?.DeepClone();
                text["cancel"] = host["settings.cancel"]?.DeepClone();
                foreach (var value in views.SelectMany(view => view.Descendants()).Attributes().Select(a => a.Value).Where(v => v.StartsWith("{DynamicResource Crew.", StringComparison.Ordinal)))
                {
                    var key = value.Substring("{DynamicResource Crew.".Length).TrimEnd('}');
                    Require(text[key]?.Type == JTokenType.String, "A Crew library string is unavailable: " + language + "/" + key);
                }
                _ = string.Format((string)text["portrait.count"]!, 12); _ = string.Format((string)text["portrait.page"]!, 1, 2);
            }
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Temp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
