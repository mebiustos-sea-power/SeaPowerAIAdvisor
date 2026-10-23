using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorTextTemplate
{
    internal const int MaxNameLength = 80;
    internal string Id { get; }
    internal string Name { get; }
    internal string Text { get; }
    internal AdvisorTextTemplate(string id, string name, string text)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new AdvisorTemplateException("templates_invalid");
        name = name.Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new AdvisorTemplateException("template_name_invalid");
        if (string.IsNullOrWhiteSpace(text) || text.Length > AdvisorConversation.MaxQuestionLength ||
            text.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new AdvisorTemplateException("template_text_invalid");
        try { _ = new UTF8Encoding(false, true).GetByteCount(id + name + text); }
        catch (EncoderFallbackException) { throw new AdvisorTemplateException("templates_invalid"); }
        Id = id; Name = name; Text = text;
    }
    internal JObject Json() => new() { ["id"] = Id, ["name"] = Name, ["text"] = Text };
}

// Templates own only reusable text. Selection does not retain a connection or consultation policy.
internal sealed class AdvisorTextTemplateStore
{
    private const int MaxBytes = 1_048_576;
    private readonly string path;
    internal AdvisorTextTemplateStore(string directory) => path = Path.Combine(directory, "templates.json");
    internal IReadOnlyList<AdvisorTextTemplate> Read()
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) throw new AdvisorTemplateException("templates_invalid");
            return Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        catch (FileNotFoundException) { return Array.Empty<AdvisorTextTemplate>(); }
        catch (DirectoryNotFoundException) { return Array.Empty<AdvisorTextTemplate>(); }
        catch (DecoderFallbackException) { throw new AdvisorTemplateException("templates_invalid"); }
        catch (IOException) { throw new AdvisorTemplateException("templates_unreadable"); }
        catch (UnauthorizedAccessException) { throw new AdvisorTemplateException("templates_unreadable"); }
    }
    internal static IReadOnlyList<AdvisorTextTemplate> Parse(string json)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 5 };
            var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read() || value.Properties().Any(p => p.Name != "schema_version" && p.Name != "templates") ||
                value["schema_version"]?.Type != JTokenType.Integer || value["schema_version"]!.ToString(Formatting.None) != "1" || value["templates"] is not JArray rows)
                throw new AdvisorTemplateException("templates_invalid");
            var entries = rows.Select(row =>
            {
                if (row is not JObject item || item.Properties().Any(p => p.Name != "id" && p.Name != "name" && p.Name != "text") ||
                    new[] { "id", "name", "text" }.Any(key => item[key]?.Type != JTokenType.String))
                    throw new AdvisorTemplateException("templates_invalid");
                return new AdvisorTextTemplate((string)item["id"]!, (string)item["name"]!, (string)item["text"]!);
            }).ToArray();
            if (entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
                throw new AdvisorTemplateException("templates_invalid");
            return Array.AsReadOnly(entries);
        }
        catch (JsonException) { throw new AdvisorTemplateException("templates_invalid"); }
    }
    internal IReadOnlyList<AdvisorTextTemplate> SaveEntry(AdvisorTextTemplate entry, bool adding)
    {
        var entries = Read();
        if (adding == entries.Any(e => e.Id == entry.Id)) throw new AdvisorTemplateException("template_selection_invalid");
        return Write(adding ? entries.Concat(new[] { entry }) : entries.Select(e => e.Id == entry.Id ? entry : e));
    }
    internal IReadOnlyList<AdvisorTextTemplate> Remove(string id)
    {
        var entries = Read();
        if (!entries.Any(e => e.Id == id)) throw new AdvisorTemplateException("template_selection_invalid");
        return Write(entries.Where(e => e.Id != id));
    }
    private IReadOnlyList<AdvisorTextTemplate> Write(IEnumerable<AdvisorTextTemplate> source)
    {
        var entries = source.ToArray();
        var json = new JObject { ["schema_version"] = 1, ["templates"] = new JArray(entries.Select(e => e.Json())) };
        var bytes = Encoding.UTF8.GetBytes(json.ToString(Formatting.Indented) + "\n");
        if (bytes.Length > MaxBytes) throw new AdvisorTemplateException("templates_invalid");
        try { AdvisorConfigFile.Write(path, bytes); }
        catch (IOException) { throw new AdvisorTemplateException("templates_write_failed"); }
        catch (UnauthorizedAccessException) { throw new AdvisorTemplateException("templates_write_failed"); }
        return Array.AsReadOnly(entries);
    }
}

internal sealed class AdvisorTemplateException : Exception
{
    internal string Code { get; }
    internal AdvisorTemplateException(string code) : base(code) => Code = code;
}
