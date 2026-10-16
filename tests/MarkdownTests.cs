using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class MarkdownTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    internal static void Run(string advisorPath, Action<string, Action> check)
    {
        check("Markdown headings and nested emphasis preserve Japanese and ordinary punctuation", () =>
        {
            var rendered = AdvisorMarkdown.Render("# 次の行動\n\n**佐世保**を守り、***索敵を優先***。\n\n未完の **太字と A_B_C は文字のまま。");
            Require(Visible(rendered).Contains("佐世保を守り、索敵を優先。"), "Formatted advice lost its content");
            var strong = rendered.Descendants(Ns + "Span").Where(e => (string?)e.Attribute("FontWeight") == "Bold").ToArray();
            Require(strong.Any(e => Visible(e) == "佐世保") && strong.Any(e => Visible(e).Contains("索敵を優先")), "Emphasis applied to the wrong text");
            Require(rendered.Descendants(Ns + "Span").Any(e => (string?)e.Attribute("FontStyle") == "Italic" && Visible(e) == "索敵を優先"), "Nested italic content was lost");
            Require(Visible(rendered).Contains("**太字と A_B_C"), "Unclosed Markdown was removed");
            var strike = AdvisorMarkdown.Render("~~以前の案~~  \n現在の案");
            Require(strike.Descendants(Ns + "Span").Any(e => (string?)e.Attribute("TextDecorations") == "Strikethrough" && Visible(e) == "以前の案") &&
                strike.Descendants(Ns + "LineBreak").Any(), "Strikethrough or an explicit line break was lost");
        });
        check("Numbered and nested lists retain their order and quoted advice stays grouped", () =>
        {
            var rendered = AdvisorMarkdown.Render("3. 識別\n   - 艦種を確認\n   - 数量を確認\n4. 迎撃\n\n> 未確認の情報です。\n>\n> **再観測**してください。\n\n---");
            var values = rendered.Descendants().Attributes("Text").Select(a => Literal(a.Value)).ToArray();
            Require(values.SequenceEqual(new[] { "3.", "識別", "•", "艦種を確認", "•", "数量を確認", "4.", "迎撃", "未確認の情報です。", "再観測", "してください。" }), "List ordering, nesting or quote content changed");
            var nested = rendered.Descendants(Ns + "Grid").Single(e => e.Elements(Ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "3."));
            Require(nested.Descendants(Ns + "Grid").Count() == 2, "Nested items became top-level steps");
        });
        check("Code preserves line breaks and indentation while markup and bindings remain literal", () =>
        {
            var code = "  if (a < b && b > 0) {\n    <Button Content=\"x\" />\n  }";
            var rendered = AdvisorMarkdown.Render("`{Binding Secret}`\n\n```text\n" + code + "\n```");
            Require(Visible(rendered).Contains("{Binding Secret}"), "Inline code was interpreted as a binding");
            Require(rendered.Descendants(Ns + "TextBlock").Any(e => Literal((string?)e.Attribute("Text") ?? "").TrimEnd('\n', '\r') == code), "Code indentation, symbols or lines changed");
            Require(!rendered.Descendants(Ns + "Button").Any(), "Code created a live UI element");
        });
        check("Markdown tables preserve headers, row associations, alignment and escaped pipes", () =>
        {
            var rendered = AdvisorMarkdown.Render("| 基地 | 進捗 |\n| :--- | ---: |\n| 佐世保 | **2/3** |\n| A\\|B | 0 |\n");
            var grid = rendered.Descendants(Ns + "Grid").Single();
            var cells = grid.Elements(Ns + "Border").ToArray();
            Require(cells.Length == 6 && cells.Where(c => (string?)c.Attribute("Grid.Row") == "1").Select(Visible).SequenceEqual(new[] { "佐世保", "2/3" }), "Table values moved into the wrong row");
            Require(cells.Where(c => (string?)c.Attribute("Grid.Column") == "1").All(c => c.Descendants(Ns + "TextBlock").All(p => (string?)p.Attribute("TextAlignment") == "Right")), "Numeric column alignment was lost");
            Require(Visible(rendered).Contains("A|B"), "An escaped pipe became a column separator");
            Require(cells.Where(c => (string?)c.Attribute("Grid.Row") == "0").All(c => c.Descendants(Ns + "TextBlock").All(p => (string?)p.Attribute("FontWeight") == "Bold")), "Headers were not distinguished from body data");
        });
        check("External references and XAML-looking text cannot become loaded images, navigation or bindings", () =>
        {
            var rendered = AdvisorMarkdown.Render("{Binding Secret}\n\n<Button Content=\"run\" />\n\n[x](javascript:alert) ![写真](https://example.invalid/p.png)\n\n&lt;艦隊&gt; &amp; 😀\u0001");
            Require(!rendered.Descendants().Any(e => e.Name.LocalName is "Button" or "Image" or "Hyperlink"), "Untrusted advice created an executable or external control");
            Require(!rendered.Descendants().Attributes().Any(a => a.Name.LocalName is "Source" or "NavigateUri"), "A reference could trigger external access");
            Require(rendered.Descendants().Attributes("Text").Any(a => a.Value == "{}{Binding Secret}"), "A literal brace was not escaped for XAML");
            Require(Visible(rendered).Contains("https://example.invalid/p.png") && Visible(rendered).Contains("<艦隊> & 😀�"), "Safe text was lost during escaping");
            _ = XDocument.Parse(rendered.ToString(SaveOptions.DisableFormatting));
        });
        check("Rendered conversations preserve raw AI history and show localized shortcut captions only", () =>
        {
            var chat = new AdvisorConversation();
            var text = AdvisorText.Load("ja");
            var question = AdvisorShortcut.NextSteps.Prepare(text[AdvisorShortcut.NextSteps.LabelKey]);
            const string answer = "## 判断\n\n**防衛**を優先します。";
            Require(chat.TryAdd(question, answer, out _), "The consultation was rejected");
            var exchange = chat.Exchanges.Single();
            var view = XDocument.Parse(AdvisorTranscript.Render(chat, text, "{Binding Name}", "empty")).Root!;
            Require(exchange.Answer == answer && exchange.Question == question.Text, "Rendering changed API history or the copied Markdown");
            Require(Visible(view).Contains(question.DisplayText) && !Visible(view).Contains(question.Text), "An internal English prompt was shown to the player");
            Require(view.Descendants(Ns + "TextBlock").Any(e => (string?)e.Attribute("Text") == "{}{Binding Name}"), "An advisor name became a binding");
            chat.Clear();
            var empty = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Advisor", "cleared")).Root!;
            Require(Visible(empty) == "cleared" && !empty.Descendants(Ns + "Button").Any(), "Clearing left an answer or copy action behind");
        });
        check("The packaged netstandard Markdown parser renders advice without an external parser DLL", () =>
        {
            using var built = AssemblyDefinition.ReadAssembly(advisorPath);
            var parser = built.MainModule.Resources.OfType<EmbeddedResource>().Single(r => r.Name == "SeaPowerAIAdvisor.Markdig.dll");
            using (var bytes = parser.GetResourceStream())
            using (var definition = AssemblyDefinition.ReadAssembly(bytes))
                Require(definition.MainModule.AssemblyReferences.All(r => r.Name == "netstandard"), "The parser acquired an unpackaged runtime dependency");
            var context = new AssemblyLoadContext("AdvisorMarkdownPackage", true);
            try
            {
                context.Resolving += (_, name) =>
                {
                    if (name.Name != "Markdig") return null;
                    using var bytes = parser.GetResourceStream();
                    return context.LoadFromStream(bytes);
                };
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(advisorPath));
                var method = assembly.GetType("SeaPowerAIAdvisor.AdvisorMarkdown", true)!.GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static)!;
                var result = (XElement)method.Invoke(null, new object[] { "# Ready\n\n**Bundled** parser." })!;
                Require(Visible(result) == "ReadyBundled parser.", "The distributed parser could not render the actual advisor code");
            }
            finally { context.Unload(); }
        });
    }

    private static string Visible(XElement element) => string.Concat(element.DescendantsAndSelf().Attributes("Text").Select(a => Literal(a.Value)));
    private static string Literal(string value) => value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
