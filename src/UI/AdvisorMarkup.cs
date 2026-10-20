using System;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SeaPowerAIAdvisor;

internal static class AdvisorMarkup
{
    internal sealed class LocalReference { }
    internal sealed class LiteralContent { }
    internal static XElement Literal(XElement content)
    {
        content.AddAnnotation(new LiteralContent());
        return content;
    }
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    internal static XElement Element(string name, params object[] content) => new(Ns + name, content);
    internal static XAttribute Text(string name, string value)
    {
        var clean = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c)) clean.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                clean.Append(c).Append(value[++i]);
            else clean.Append('�');
        }
        var literal = clean.ToString();
        return new XAttribute(name, literal.StartsWith("{", StringComparison.Ordinal) ? "{}" + literal : literal);
    }
}
