using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor.Shared;

// The native INI format owns the file. This section contains only the caller's opaque bytes.
internal sealed class SaveDataSection
{
    private readonly string name;
    internal SaveDataSection(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.IndexOfAny(new[] { '[', ']', '\r', '\n' }) >= 0)
            throw new ArgumentException("A valid save-data owner is required.", nameof(owner));
        name = "ModData." + owner;
    }
    internal string? Read(IDictionary<string, Dictionary<string, string>> sections)
    {
        if (!sections.TryGetValue(name, out var section)) return null;
        if (!section.TryGetValue("Payload", out var payload)) throw new FormatException("The MOD save section has no payload: " + name);
        return payload;
    }
    internal void Write(IDictionary<string, Dictionary<string, string>> sections, string? payload)
    {
        if (payload == null) sections.Remove(name);
        else sections[name] = new Dictionary<string, string> { ["Payload"] = payload };
    }
    internal static string? Encode(byte[]? data) => data == null ? null : Convert.ToBase64String(data);
    internal static byte[]? Decode(string? payload) => payload == null ? null : Convert.FromBase64String(payload);
}
