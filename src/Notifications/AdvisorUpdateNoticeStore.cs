using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Read receipts are local user state, independent of settings edits, missions and AI conversations.
internal sealed class AdvisorUpdateNoticeStore
{
    private readonly string path;
    internal AdvisorUpdateNoticeStore(string directory) => path = Path.Combine(directory, "read-notices.json");
    internal AdvisorUpdateNotice? Unread(AdvisorUpdateNotice? notice, bool preview = false) =>
        notice != null && (preview || !Read().Contains(notice.Id)) ? notice : null;
    internal void MarkRead(AdvisorUpdateNotice notice, bool preview = false)
    {
        if (preview) return;
        var ids = Read();
        if (!ids.Add(notice.Id)) return;
        var value = new JObject { ["read_notice_ids"] = new JArray(ids.OrderBy(id => id, StringComparer.Ordinal)) };
        var bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + "\n");
        if (bytes.Length > 16_384) throw new AdvisorUpdateNoticeException("notice_state_write_failed", new InvalidDataException("Notice read receipts are too large."));
        try { AdvisorConfigFile.Write(path, bytes); }
        catch (IOException error) { throw new AdvisorUpdateNoticeException("notice_state_write_failed", error); }
        catch (UnauthorizedAccessException error) { throw new AdvisorUpdateNoticeException("notice_state_write_failed", error); }
    }
    private HashSet<string> Read()
    {
        try
        {
            if (Directory.Exists(path)) throw new IOException("The notice receipt path is a directory.");
            var data = AdvisorConfigFile.Backup(path);
            if (data == null) return new HashSet<string>(StringComparer.Ordinal);
            using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(data)))
                { DateParseHandling = DateParseHandling.None, MaxDepth = 3 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value.Properties().Any(p => p.Name != "read_notice_ids") || value["read_notice_ids"] is not JArray ids ||
                ids.Any(id => id.Type != JTokenType.String || !AdvisorUpdateNotice.ValidId((string)id!)))
                throw new InvalidDataException("Invalid notice read receipts.");
            return new HashSet<string>(ids.Select(id => (string)id!), StringComparer.Ordinal);
        }
        catch (JsonException error) { throw new AdvisorUpdateNoticeException("notice_state_invalid", error); }
        catch (DecoderFallbackException error) { throw new AdvisorUpdateNoticeException("notice_state_invalid", error); }
        catch (InvalidDataException error) { throw new AdvisorUpdateNoticeException("notice_state_invalid", error); }
        catch (IOException error) { throw new AdvisorUpdateNoticeException("notice_state_unreadable", error); }
        catch (UnauthorizedAccessException error) { throw new AdvisorUpdateNoticeException("notice_state_unreadable", error); }
    }
}

internal sealed class AdvisorUpdateNoticeException : Exception
{
    internal string Code { get; }
    internal AdvisorUpdateNoticeException(string code, Exception inner) : base(code, inner) => Code = code;
}
