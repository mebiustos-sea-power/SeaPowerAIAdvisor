using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorLayoutDefinition
{
    internal enum Section { Profile, Shortcuts, Conversation, Composer, Status }

    internal sealed class Row(string height, Column[] columns)
    {
        internal string Height { get; } = height;
        internal IReadOnlyList<Column> Columns { get; } = columns;
    }

    internal sealed class Column(string width, Section[] sections, bool scroll)
    {
        internal string Width { get; } = width;
        internal IReadOnlyList<Section> Sections { get; } = sections;
        internal bool Scroll { get; } = scroll;
    }

    internal IReadOnlyList<Row> Rows { get; }

    private AdvisorLayoutDefinition(Row[] rows) => Rows = rows;

    internal static AdvisorLayoutDefinition Load(string gameDataPath)
    {
        var path = Path.Combine(gameDataPath, "SeaPowerAIAdvisor", "conversation-layout.json");
        Stream? custom;
        try { custom = File.OpenRead(path); }
        catch (FileNotFoundException) { custom = null; }
        catch (DirectoryNotFoundException) { custom = null; }
        if (custom != null)
        {
            using (custom) return Read(custom, path);
        }

        return Standard();
    }

    internal static AdvisorLayoutDefinition Standard()
    {
        const string resource = "SeaPowerAIAdvisor.conversation-layout.json";
        using var standard = typeof(AdvisorLayoutDefinition).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("The embedded advisor conversation layout is missing.", resource);
        return Read(standard, resource);
    }

    private static AdvisorLayoutDefinition Read(Stream stream, string source)
    {
        try
        {
            using var input = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 };
            var layout = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("Unexpected content after the layout.");

            var seen = new HashSet<Section>();
            var rows = RequiredArray(Fields(layout, "rows"), "rows").Select(row =>
            {
                var fields = Fields(row, "height", "columns");
                return new Row(TrackSize(fields, "height"),
                    RequiredArray(fields, "columns").Select(column => ReadColumn(column, seen)).ToArray());
            }).ToArray();

            var missing = Enum.GetValues(typeof(Section)).Cast<Section>().Where(section => !seen.Contains(section)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException("Missing sections: " + string.Join(", ", missing.Select(section => section.ToString().ToLowerInvariant())) + ".");
            return new AdvisorLayoutDefinition(rows);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Invalid advisor conversation layout '{source}': {error.Message}", error);
        }
    }

    private static Column ReadColumn(JToken token, HashSet<Section> seen)
    {
        var column = Fields(token, "width", "sections", "scroll");
        var width = TrackSize(column, "width");
        var scroll = column["scroll"];
        if (scroll != null && scroll.Type != JTokenType.Boolean)
            throw new InvalidDataException($"'{scroll.Path}' must be true or false.");
        if (column["sections"] is not JArray items)
            throw new InvalidDataException($"'{column.Path}.sections' must be an array.");

        var sections = items.Select(item =>
        {
            var section = (item.Type == JTokenType.String ? (string?)item : null) switch
            {
                "profile" => Section.Profile,
                "shortcuts" => Section.Shortcuts,
                "conversation" => Section.Conversation,
                "composer" => Section.Composer,
                "status" => Section.Status,
                _ => throw new InvalidDataException($"Unknown section at '{item.Path}'.")
            };
            if (!seen.Add(section))
                throw new InvalidDataException($"Section '{(string?)item}' is repeated at '{item.Path}'.");
            return section;
        }).ToArray();
        return new Column(width, sections, (bool?)scroll ?? false);
    }

    private static JObject Fields(JToken token, params string[] allowed)
    {
        if (token is not JObject obj)
            throw new InvalidDataException($"'{token.Path}' must be an object.");
        foreach (var property in obj.Properties())
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"Unknown property '{property.Path}'.");
        return obj;
    }

    private static JArray RequiredArray(JObject obj, string field) =>
        obj[field] is JArray { Count: > 0 } items ? items :
            throw new InvalidDataException($"'{FieldPath(obj, field)}' must be a non-empty array.");

    private static string TrackSize(JObject obj, string field)
    {
        var value = obj[field]?.Type == JTokenType.String ? (string?)obj[field] : null;
        if (value is "Auto" or "*") return value;
        if (value != null)
        {
            var number = value.EndsWith("*", StringComparison.Ordinal) ? value.Substring(0, value.Length - 1) : value;
            if (float.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)
                && size > 0 && !float.IsInfinity(size) && number.IndexOf('\0') < 0)
                return value;
        }
        throw new InvalidDataException($"'{FieldPath(obj, field)}' must be \"Auto\", \"*\", a positive size or a positive star weight.");
    }

    private static string FieldPath(JObject obj, string field) => obj.Path.Length == 0 ? field : obj.Path + "." + field;
}
