using System;
using System.IO;
using System.Xml;
using SeaPower;

namespace SeaPowerAIAdvisor.Shared;

// Game-thread capture of the public XML briefing; failures remain logged and explicitly unavailable.
internal static class BattleBriefingSource
{
    internal static (string Text, bool Unavailable, bool FromBase) ReadXml(string language, Action<Exception> warn)
    {
        var xmlBriefing = "";
        var briefingUnavailable = false;
        var briefingFromBase = false;
        try
        {
            var files = FileManager.Instance;
            // Read only public pane-selection keys and source-file metadata, never unit definitions or triggers.
            var ini = SceneCreator.Instance.MissionIni;
            string? ResolveFile(string path)
            {
                var resolved = System.IO.Path.IsPathRooted(path) ? path : files.GetFile(path, null);
                return !string.IsNullOrEmpty(resolved) && File.Exists(resolved) ? resolved : null;
            }
            var briefing = BattleBriefingReader.Read(files.GetRelativePath(Globals.currentMissionFilePath) ?? "", language,
                (section, key, fallback) => ini.readValue(section, key, fallback, true), ResolveFile, File.ReadAllText,
                () =>
                {
                    var baseFile = ini.readValue("File", "BaseFile", "", false);
                    if (string.IsNullOrWhiteSpace(baseFile)) return null;
                    var basePath = ResolveFile(baseFile)
                        ?? throw new FileNotFoundException("The saved mission's BaseFile is unavailable.", baseFile);
                    var original = Utils.openIniFile(basePath, true, true)
                        ?? throw new InvalidDataException("The original mission's briefing settings could not be opened.");
                    return new BattleBriefingTemplate(files.GetRelativePath(basePath) ?? "",
                        (section, key, fallback) => original.readValue(section, key, fallback, true));
                });
            xmlBriefing = briefing.Text;
            briefingFromBase = briefing.FromBase;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
            error is XmlException || error is NotSupportedException || error is InvalidDataException)
        {
            briefingUnavailable = true;
            warn(error);
        }
        return (xmlBriefing, briefingUnavailable, briefingFromBase);
    }
}
