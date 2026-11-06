using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor.Crew;
using SeaPowerAIAdvisor.Shared;

internal static class CrewSampleCharacterTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("First Crew use imports four ordinary editable registrations with persistent portrait references", () => Temp(directory =>
        {
            var portraits = new CrewPortraitLibrary(directory); var library = CrewSampleCharacters.Open(directory, portraits);
            Require(library.Characters.Count == 4 && portraits.Ids().Count == 4, "Initial samples or portraits were not imported");
            Require(library.Characters.Select(c => c.Gender + "/" + c.AssignmentTarget).OrderBy(v => v).SequenceEqual(
                new[] { "female/aircraft", "female/ship", "male/aircraft", "male/ship" }), "Sample roles or genders were lost");
            Require(library.Characters.All(c => c.Nationality == "US" && c.Name == "" && c.Rank == "" && c.Personality == "" &&
                c.AiAppointment == "normal" && !c.AllowDuplicates), "Initial definitions no longer match the four registered examples");
            foreach (var character in library.Characters)
                Require(portraits.Read(character.PortraitId!)!.SequenceEqual(Resource(character.PortraitId!)), "An imported image changed or points at the wrong portrait");
            var reopened = CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory));
            Require(reopened.Characters.Select(c => c.Json().ToString()).SequenceEqual(library.Characters.Select(c => c.Json().ToString())), "Reopening changed sample identities or fields");
        }));
        check("Sample edits, character deletion and image replacement or deletion survive later opens without writes", () => Temp(directory =>
        {
            var portraits = new CrewPortraitLibrary(directory); var library = CrewSampleCharacters.Open(directory, portraits);
            var first = library.Characters[0]; var deleted = library.Characters[1]; var missingImage = library.Characters[2].PortraitId!;
            library.Save(new CrewCharacter(first.Id, "User name", "User rank", "User personality", portraitId: first.PortraitId, aiAppointment: "excluded", allowDuplicates: true));
            library.Delete(deleted.Id); portraits.Delete(deleted.PortraitId!); portraits.Delete(missingImage);
            var replacement = CrewTests.PortraitHeader(128, 256); portraits.Replace(first.PortraitId!, replacement);
            var registry = File.ReadAllBytes(Registry(directory)); var images = portraits.Ids().ToArray();
            var reopened = CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory));
            Require(reopened.Characters.Count == 3 && reopened.Find(deleted.Id) == null && reopened.Find(first.Id)!.Name == "User name" &&
                reopened.Find(first.Id)!.AllowDuplicates && reopened.Find(first.Id)!.AiAppointment == "excluded", "A later open reverted user changes");
            Require(File.ReadAllBytes(Registry(directory)).SequenceEqual(registry) && portraits.Ids().SequenceEqual(images) &&
                portraits.Read(first.PortraitId!)!.SequenceEqual(replacement) && portraits.Read(missingImage) == null, "A later open rewrote the registry or restored or replaced a user image");
        }));
        check("Deleting every character and portrait leaves an intentional empty library across repeated opens", () => Temp(directory =>
        {
            var portraits = new CrewPortraitLibrary(directory); var library = CrewSampleCharacters.Open(directory, portraits);
            foreach (var character in library.Characters.ToArray()) { library.Delete(character.Id); portraits.Delete(character.PortraitId!); }
            var registry = File.ReadAllBytes(Registry(directory));
            for (var i = 0; i < 2; i++)
                Require(CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory)).Characters.Count == 0 && portraits.Ids().Count == 0 &&
                    registry.SequenceEqual(File.ReadAllBytes(Registry(directory))), "An empty saved registry was mistaken for first use");
        }));
        check("An existing unrelated registration is authoritative and does not acquire bundled sample images", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var custom = new CrewCharacter(Guid.NewGuid().ToString("N"), "Existing character"); library.Save(custom);
            var before = File.ReadAllBytes(Registry(directory)); var portraits = new CrewPortraitLibrary(directory);
            var reopened = CrewSampleCharacters.Open(directory, portraits);
            Require(reopened.Characters.Count == 1 && reopened.Find(custom.Id)!.Name == custom.Name && portraits.Ids().Count == 0 &&
                File.ReadAllBytes(Registry(directory)).SequenceEqual(before), "Opening an existing library merged defaults or wrote images");
        }));
        check("A partial portrait import is retryable without publishing a partial registry or overwriting existing images", () => Temp(directory =>
        {
            var samples = CrewCharacterLibrary.Read(Resource("characters.json")); var portraits = new CrewPortraitLibrary(directory);
            var blocked = Image(directory, samples[1].PortraitId!); Directory.CreateDirectory(blocked);
            Throws<CrewPortraitLibraryException>(() => CrewSampleCharacters.Open(directory, portraits));
            Require(!File.Exists(Registry(directory)) && portraits.Ids().Count == 1, "An incomplete import was marked initialized");
            var replacement = CrewTests.PortraitHeader(128, 128); portraits.Replace(samples[0].PortraitId!, replacement); Directory.Delete(blocked);
            var retried = CrewSampleCharacters.Open(directory, portraits);
            Require(retried.Characters.Count == 4 && portraits.Ids().Count == 4 && portraits.Read(samples[0].PortraitId!)!.SequenceEqual(replacement),
                "Retry failed, duplicated assets or overwrote a previously imported image");
        }));
        check("A failed initial registry write publishes no characters and remains retryable until a successful commit", () => Temp(directory =>
        {
            var library = new CrewCharacterLibrary(directory); var samples = CrewCharacterLibrary.Read(Resource("characters.json"));
            Throws<CrewException>(() => library.Initialize(() => { Directory.CreateDirectory(Registry(directory)); return samples; }));
            Require(library.Characters.Count == 0 && !File.Exists(Registry(directory)), "Failed initialization published characters");
            Directory.Delete(Registry(directory)); library.Initialize(() => samples);
            library.Initialize(() => throw new InvalidOperationException("Existing data must not be initialized again."));
            Require(new CrewCharacterLibrary(directory).Characters.Count == 4, "Successful retry did not persist the full registry");
        }));
        check("Invalid existing registries remain visible errors and are never replaced with samples", () => Temp(directory =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Registry(directory))!);
            foreach (var invalid in new[] { "", "{", "{\"format_version\":99,\"characters\":[]}" })
            {
                File.WriteAllText(Registry(directory), invalid);
                Throws<CrewException>(() => CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory)));
                Require(File.ReadAllText(Registry(directory)) == invalid && new CrewPortraitLibrary(directory).Ids().Count == 0, "Invalid data was hidden by default initialization");
            }
            File.Delete(Registry(directory)); Directory.CreateDirectory(Registry(directory));
            Throws<CrewException>(() => CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory)));
            Require(Directory.Exists(Registry(directory)), "An invalid library path was replaced");
        }));
        check("An unreadable existing sample image is surfaced rather than silently overwritten during initial import", () => Temp(directory =>
        {
            var sample = CrewCharacterLibrary.Read(Resource("characters.json"))[0]; var path = Image(directory, sample.PortraitId!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); var invalid = new byte[] { 1, 2, 3 }; File.WriteAllBytes(path, invalid);
            Throws<PortraitDataException>(() => CrewSampleCharacters.Open(directory, new CrewPortraitLibrary(directory)));
            Require(File.ReadAllBytes(path).SequenceEqual(invalid) && !File.Exists(Registry(directory)), "Image corruption was hidden by default initialization");
        }));
    }
    internal static void RunBundle(string advisorPath, Action<string, Action> check)
    {
        check("The distributed Advisor assembly contains the complete sample registry and its correctly sized portrait payloads", () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(advisorPath);
            byte[] Read(string name) => assembly.MainModule.Resources.OfType<EmbeddedResource>()
                .Single(r => r.Name == "SeaPowerAIAdvisor.Crew.Samples." + name).GetResourceData();
            var samples = CrewCharacterLibrary.Read(Read("characters.json"));
            Require(samples.Count == 4, "The packaged registry is incomplete");
            foreach (var character in samples)
            {
                var bytes = Read(character.PortraitId!); var image = PortraitData.Inspect(bytes);
                Require(image.Width <= CrewPortraitLibrary.MaximumDimension && image.Height <= CrewPortraitLibrary.MaximumDimension &&
                    bytes.SequenceEqual(Resource(character.PortraitId!)), "A packaged portrait is unusable or differs from the tested image");
            }
        });
    }
    private static string Registry(string directory) => Path.Combine(directory, "Crew", "characters.json");
    private static string Image(string directory, string id) => Path.Combine(directory, "Crew", "PortraitLibrary", id);
    private static byte[] Resource(string name)
    {
        using var stream = typeof(CrewSampleCharacterTests).Assembly.GetManifestResourceStream("SeaPowerAIAdvisor.Crew.Samples." + name)!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Temp(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "crew-samples-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { action(directory); } finally { Directory.Delete(directory, true); }
    }
}
