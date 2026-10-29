using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Translation;

// UI placement is local user state, independent of connection settings and their Save button.
internal sealed class TranslationPositionStore
{
    private readonly string path;
    internal TranslationPositionStore(string path) => this.path = path;
    internal (float X, float Y)? Read()
    {
        if (Directory.Exists(path)) throw new IOException("The translation position path is a directory.");
        var bytes = ConfigFile.Backup(path);
        if (bytes == null) return null;
        using var source = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
        using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 2 };
        var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || value.Properties().Any(p => p.Name != "x" && p.Name != "y"))
            throw new InvalidDataException("Invalid translation button position.");
        var x = Coordinate(value["x"]); var y = Coordinate(value["y"]);
        Validate(x, y);
        return (x, y);
    }
    internal void Save(float x, float y)
    {
        Validate(x, y);
        var value = new JObject { ["x"] = x, ["y"] = y };
        ConfigFile.Write(path, Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented) + "\n"));
    }
    private static float Coordinate(JToken? value)
    {
        if (value == null || value.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new InvalidDataException("Invalid translation button coordinate.");
        return value.Value<float>();
    }
    private static void Validate(float x, float y)
    {
        if (float.IsNaN(x) || float.IsInfinity(x) || x < 0 || float.IsNaN(y) || float.IsInfinity(y) || y < 0)
            throw new InvalidDataException("Translation button coordinates must be finite and nonnegative.");
    }
}
