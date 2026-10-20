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
            var contacts = AdvisorMarkdown.Render("敵確認接触は**Contact 7008（識別済み）**と**Contact 7018（未識別）**の2点。");
            Require(Visible(contacts) == "敵確認接触はContact 7008（識別済み）とContact 7018（未識別）の2点。" &&
                contacts.Descendants(Ns + "Span").Where(e => (string?)e.Attribute("FontWeight") == "Bold")
                    .Select(Visible).SequenceEqual(new[] { "Contact 7008（識別済み）", "Contact 7018（未識別）" }),
                "Japanese punctuation left emphasis markers visible or emphasized only the joining word");
            var english = AdvisorMarkdown.Render("**Contact 7008 (identified)** and *Contact 7018 (unknown)*.");
            Require(Visible(english) == "Contact 7008 (identified) and Contact 7018 (unknown)." &&
                english.Descendants(Ns + "Span").Single(e => (string?)e.Attribute("FontWeight") == "Bold") is var bold &&
                Visible(bold) == "Contact 7008 (identified)" &&
                english.Descendants(Ns + "Span").Single(e => (string?)e.Attribute("FontStyle") == "Italic") is var italic &&
                Visible(italic) == "Contact 7018 (unknown)", "CJK-friendly emphasis changed English strong or italic text");
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
        check("Text-size changes rebuild existing Markdown conversation without changing content or formatting structure", () =>
        {
            var chat = new AdvisorConversation();
            const string question = "次の行動を詳しく。\n既存の会話です。";
            const string answer = "# 優先事項\n\n## 防衛\n\n**佐世保**を守る。\n\n> 未確認の情報\n\n1. 索敵\n\n```text\n  radar < unknown\n```\n\n| 部隊 | 状態 |\n| --- | --- |\n| 護衛 | 待機 |";
            Require(chat.TryAdd(question, answer, out _), "The existing conversation could not be prepared");
            var text = AdvisorText.Load("ja");
            var standard = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Michael Reed", "empty", textSize: AdvisorTextSize.Standard)).Root!;
            var originalSizes = standard.DescendantsAndSelf().Attributes()
                .Where(a => a.Name.LocalName is "FontSize" or "TextElement.FontSize")
                .Select(a => double.Parse(a.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Require(originalSizes.Length > 5, "The fixture does not exercise conversation and Markdown typography");
            foreach (var size in new[] { AdvisorTextSize.Small, AdvisorTextSize.Large })
            {
                var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Michael Reed", "empty", textSize: size)).Root!;
                var resized = rendered.DescendantsAndSelf().Attributes()
                    .Where(a => a.Name.LocalName is "FontSize" or "TextElement.FontSize")
                    .Select(a => double.Parse(a.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Require(resized.Length == originalSizes.Length && resized.Zip(originalSizes).All(pair =>
                    size == AdvisorTextSize.Small ? pair.First < pair.Second : pair.First > pair.Second),
                    "Part of the existing conversation kept its old size");
                var heading = rendered.Descendants(Ns + "TextBlock").First(e => Visible(e) == "優先事項");
                var body = rendered.Descendants(Ns + "StackPanel").Single(e => e.Attribute("TextElement.FontSize") != null);
                Require(double.Parse(heading.Attribute("FontSize")!.Value, System.Globalization.CultureInfo.InvariantCulture) >
                    double.Parse(body.Attribute("TextElement.FontSize")!.Value, System.Globalization.CultureInfo.InvariantCulture),
                    "Resizing removed the heading/body hierarchy");
                var before = new XElement(standard);
                foreach (var element in new[] { before, rendered })
                    element.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "FontSize" or "TextElement.FontSize").Remove();
                Require(XNode.DeepEquals(before, rendered), "Resizing changed message text, emphasis, colors, table layout or actions");
            }
            Require(chat.Exchanges.Single().Question == question && chat.Exchanges.Single().Answer == answer,
                "Changing the display size altered the saved AI conversation");
        });
        check("Track links preserve question and Markdown content while targeting only known four-digit contacts", () =>
        {
            var known = new System.Collections.Generic.HashSet<int> { 7008, 7018 };
            var tracks = new AdvisorUnitLinks(known.Contains);
            var chat = new AdvisorConversation();
            const string question = "接触7008と7018。70080／17008／6999／7999。<Button />";
            const string answer = "## 接触 **7008**\n\n1. Track 7018と7008を確認。\n\n| 接触 | 評価 |\n| --- | --- |\n| 7018 | **7008** |\n\n`7008`\n\n```text\n 7008\n```";
            Require(chat.TryAdd(question, answer, out _), "The track consultation was rejected");
            var text = AdvisorText.Load("ja");
            var plain = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Michael Reed", "empty")).Root!;
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Michael Reed", "empty", links: tracks)).Root!;
            Require(Visible(rendered) == Visible(plain), "Adding track actions changed visible text or code whitespace");
            var hyperlinks = rendered.Descendants(Ns + "Hyperlink").ToArray();
            Require(hyperlinks.Length == 9 && tracks.Links.Select(link => link.Name).Distinct().Count() == 9,
                "Repeated tracks collided, a longer number became a track, or a track was missed");
            foreach (var link in tracks.Links)
            {
                var element = hyperlinks.Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == link.Name);
                Require(Visible(element) == link.UnitId.ToString() && element.Attribute("Foreground") == null &&
                    (string?)element.Attribute("Style") == "{DynamicResource AdvisorTrackLinkStyle}",
                    "The displayed number/action target changed or a local text color prevented the shared hover style");
                Require((string?)element.Attribute("TextDecorations") == "None" && element.Attribute("ToolTip") == null &&
                    element.Attribute("NavigateUri") == null && element.Attribute("Cursor") == null,
                    "A track acquired an underline, tooltip, cursor override or external navigation");
            }
            Require(!rendered.Descendants(Ns + "InlineUIContainer").Any(),
                "Track decoration inserted an inline UI object that could overlap the number during hover");
            Require(hyperlinks.Any(e => e.Ancestors(Ns + "Span").Any(a => (string?)a.Attribute("FontWeight") == "Bold")),
                "Track decoration lost Markdown emphasis");
            Require(chat.Exchanges.Single().Question == question && chat.Exchanges.Single().Answer == answer,
                "Interactive display altered copied questions or AI history");
        });
        check("Track guidance is limited to empty supported conversations and disappeared contacts become plain text", () =>
        {
            var known = true;
            var text = AdvisorText.Load("en");
            var chat = new AdvisorConversation();
            var empty = AdvisorTranscript.Render(chat, text, "Michael Reed", "empty", links: new AdvisorUnitLinks(n => known && n == 7008));
            Require(Visible(XDocument.Parse(empty).Root!).Contains(text["transcript.track_navigation"]), "The initial track instructions were missing");
            var unsupported = AdvisorTranscript.Render(chat, text, "Michael Reed", "empty");
            Require(!Visible(XDocument.Parse(unsupported).Root!).Contains(text["transcript.track_navigation"]), "An unsupported mode advertised track navigation");
            Require(chat.TryAdd("Contact 7008", "**7008**", out _), "The known-track consultation was rejected");
            var active = AdvisorTranscript.Render(chat, text, "Michael Reed", "empty", links: new AdvisorUnitLinks(n => known && n == 7008));
            Require(!Visible(XDocument.Parse(active).Root!).Contains(text["transcript.track_navigation"]), "Initial instructions stayed above an active conversation");
            known = false;
            var refreshed = XDocument.Parse(AdvisorTranscript.Render(chat, text, "Michael Reed", "empty",
                links: new AdvisorUnitLinks(n => known && n == 7008))).Root!;
            Require(!refreshed.Descendants(Ns + "Hyperlink").Any() && Visible(refreshed) == Visible(XDocument.Parse(active).Root!),
                "A missing contact stayed actionable or its message was removed");
        });
        check("Own-unit links resolve complete names and one-token omissions while preserving Markdown and original history", () =>
        {
            var units = new[] { (Id: 101, Name: "カリフォルニア CGN-36"), (Id: 102, Name: "South Carolina CGN-37"),
                (Id: 103, Name: "K-64"), (Id: 104, Name: "Test (A)+ 42") };
            var known = new System.Collections.Generic.HashSet<int>(units.Select(unit => unit.Id).Append(7008));
            const string question = "カリフォルニアについて";
            const string answer = "**カリフォルニア CGN-36**、カリフォルニア。South Carolina CGN-37とSouth Carolina。K-64。Test (A)+ 42とTest (A)+。CGN-36のみやSouthだけは対象外。7008。";
            var chat = new AdvisorConversation(); Require(chat.TryAdd(question, answer, out _), "The fixture consultation was rejected");
            var links = new AdvisorUnitLinks(known.Contains, units);
            var plain = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("ja"), "Michael Reed", "empty")).Root!;
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("ja"), "Michael Reed", "empty", links: links)).Root!;
            Require(Visible(rendered) == Visible(plain), "Own-unit actions changed the displayed message");
            Require(links.Links.Select(link => link.UnitId).SequenceEqual(new[] { 101, 101, 101, 102, 102, 103, 104, 104, 7008 }),
                "A complete or shortened name resolved to the wrong unit, or an unsupported name fragment became actionable");
            var targets = rendered.Descendants(Ns + "Hyperlink").ToArray();
            Require(targets.Count(e => (string?)e.Attribute("Style") == "{DynamicResource AdvisorOwnUnitLinkStyle}") == 8 &&
                (string?)targets.Last().Attribute("Style") == "{DynamicResource AdvisorTrackLinkStyle}",
                "Own names and contact numbers did not receive their respective display styles");
            Require(targets.All(e => e.Attribute("ToolTip") == null && e.Attribute("Cursor") == null &&
                (string?)e.Attribute("TextDecorations") == "None") && !rendered.Descendants(Ns + "InlineUIContainer").Any(),
                "Own-name links added an icon or intrusive decoration");
            Require(chat.Exchanges.Single().Question == question && chat.Exchanges.Single().Answer == answer,
                "Own-name links changed the copied or transmitted history");
        });
        check("Own-name matching prefers the longest name and leaves ambiguous, embedded and disappeared names inactive", () =>
        {
            var units = new[] { (1, "Falcon One"), (2, "Falcon Two"), (3, "South Carolina CGN-37"),
                (4, "South Carolina CGN-38"), (5, "Alpha"), (6, "Alpha Bravo"), (7, "K-64"), (7, "K-64") };
            var known = true;
            const string message = "Falcon / Falcon One / Falcon Two / South Carolina / South Carolina CGN-37 / South Carolina CGN-38 / Alpha / Alpha Bravo / K-64 / XK-64 / K-640 / Falcon OneTwo";
            var links = new AdvisorUnitLinks(_ => known, units);
            var content = AdvisorMarkdown.Render(message);
            links.Decorate(content);
            Require(links.Links.Select(link => link.UnitId).SequenceEqual(new[] { 1, 2, 3, 4, 6, 7 }),
                "An ambiguous alias became active, a complete name was truncated, or a name inside a longer word matched");
            Require(Visible(content) == message, "Skipping ambiguous names changed the text");
            known = false;
            var missing = new AdvisorUnitLinks(_ => known, units);
            Require(!missing.Decorate(AdvisorMarkdown.Render(message)).Descendants(Ns + "Hyperlink").Any(),
                "A disappeared own unit remained actionable");
        });
        check("Own-unit and contact links can be independently hidden without changing conversation text", () =>
        {
            const string message = "カリフォルニア CGN-36、カリフォルニア、7008";
            foreach (var own in new[] { false, true })
                foreach (var contacts in new[] { false, true })
                {
                    var links = new AdvisorUnitLinks(_ => true, own ? new[] { (101, "カリフォルニア CGN-36") } : null, contacts);
                    var result = links.Decorate(AdvisorMarkdown.Render(message));
                    Require(Visible(result) == message && links.Links.Count(link => link.UnitId == 101) == (own ? 2 : 0) &&
                        links.Links.Count(link => link.UnitId == 7008) == (contacts ? 1 : 0),
                        "A display toggle changed text or also affected the other class of links");
                }
        });
        check("Explicit unit links retain their consultation targets across later answers and coexist with name and track fallbacks", () =>
        {
            var chat = new AdvisorConversation();
            const string firstAnswer = "# [**P-3C**](unit:own_unit_1)\n\n[カリフォルニア](unit:own_unit_2)と7008。";
            const string secondAnswer = "[P-3C](unit:own_unit_1)とP-3C 第9航空隊 'Seastar'。";
            var map = new System.Collections.Generic.List<(string, int)> { ("own_unit_1", 101), ("own_unit_2", 104) };
            var first = chat.Begin(new AdvisorQuestion("First")); first.SetUnitReferences(new AdvisorUnitReferences(map)); first.Complete(firstAnswer);
            map[0] = ("own_unit_1", 999);
            var second = chat.Begin(new AdvisorQuestion("Second"));
            second.SetUnitReferences(new AdvisorUnitReferences(new[] { ("own_unit_1", 202) })); second.Complete(secondAnswer);
            var known = new System.Collections.Generic.HashSet<int> { 101, 104, 202, 303, 400, 7008 };
            var links = new AdvisorUnitLinks(known.Contains, new[] { (303, "P-3C 第9航空隊 'Seastar'"), (400, "カリフォルニア CGN-36") });
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("ja"), "Michael Reed", "empty", links: links)).Root!;
            Require(links.Links.Select(link => link.UnitId).SequenceEqual(new[] { 101, 104, 7008, 202, 303 }),
                "An old reference was rebound to a new consultation, or an explicit destination was replaced by name matching");
            var anchors = rendered.Descendants(Ns + "Hyperlink").ToArray();
            Require(anchors.Length == 5 && anchors.All(link => !link.Descendants(Ns + "Hyperlink").Any()) &&
                anchors.First().Descendants(Ns + "Span").Any(span => (string?)span.Attribute("FontWeight") == "Bold"),
                "An explicit label lost Markdown formatting or became nested links");
            Require(!Visible(rendered).Contains("unit:") && !Visible(rendered).Contains("own_unit_") &&
                chat.Exchanges.First().Answer == firstAnswer && chat.Exchanges.Last().Answer == secondAnswer,
                "A local ID became visible or rendering rewrote copied/API history");
            var retry = chat.Begin(new AdvisorQuestion("Retry")); retry.SetUnitReferences(first.UnitReferences); retry.Cancel();
            chat.Retry(retry);
            Require(retry.UnitReferences.Resolve("own_unit_1") == null, "Retry retained targets from the failed capture");
        });
        check("Unresolved explicit IDs remain plain labels and cannot be retargeted through their names or numbers", () =>
        {
            const string answer = "[P-3C](unit:missing) / [7008](unit:missing) / [P-3C](unit:) / [P-3C](unit:own_unit_9) / P-3C / 7008";
            var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Assess"));
            turn.SetUnitReferences(new AdvisorUnitReferences(new[] { ("own_unit_9", 999) })); turn.Complete(answer);
            var links = new AdvisorUnitLinks(id => id == 303 || id == 7008, new[] { (303, "P-3C Seastar") });
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty", links: links)).Root!;
            Require(links.Links.Select(link => link.UnitId).SequenceEqual(new[] { 303, 7008 }),
                "An unknown or disappeared explicit target fell through to a different unit");
            Require(Visible(rendered).Contains("P-3C / 7008 / P-3C / P-3C / P-3C / 7008") &&
                !Visible(rendered).Contains("unit:") && !rendered.Descendants().Attributes("NavigateUri").Any(),
                "An unresolved destination leaked implementation text or triggered URI navigation");
            var unsupported = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty")).Root!;
            Require(!unsupported.Descendants(Ns + "Hyperlink").Any() && !Visible(unsupported).Contains("unit:"),
                "An unsupported view created actionable local references or exposed their IDs");
        });
        check("Explicit references obey link visibility independently and preserve external and literal Markdown behavior", () =>
        {
            var chat = new AdvisorConversation(); var turn = chat.Begin(new AdvisorQuestion("Assess"));
            turn.SetUnitReferences(new AdvisorUnitReferences(new[] { ("own_unit_1", 101) }));
            turn.Complete("[7008](unit:own_unit_1) / P-3C / 7008");
            foreach (var own in new[] { false, true })
                foreach (var contacts in new[] { false, true })
                {
                    var links = new AdvisorUnitLinks(_ => true, new[] { (303, "P-3C Seastar") }, contacts, own);
                    var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty", links: links)).Root!;
                    Require(links.Links.Count(link => link.UnitId == 101) == (own ? 1 : 0) &&
                        links.Links.Count(link => link.UnitId == 303) == (own ? 1 : 0) &&
                        links.Links.Count(link => link.UnitId == 7008) == (contacts ? 1 : 0) && !Visible(rendered).Contains("unit:"),
                        "An explicit reference ignored its display setting or was reinterpreted as a contact number");
                }
            var literal = AdvisorMarkdown.Render("`[P-3C](unit:own_unit_1)`\n\n[x](https://example.invalid) ![画像](https://example.invalid/p.png)");
            Require(Visible(literal).Contains("[P-3C](unit:own_unit_1)") && !literal.Descendants(Ns + "Hyperlink").Any() &&
                !literal.Descendants(Ns + "Image").Any(), "Literal code or external references acquired actions");
        });
        check("Weapon links use stable public keys across replies while preserving formatted labels and original history", () =>
        {
            var chat = new AdvisorConversation();
            const string first = "[**RIM-66E**](weapon:usn_rim-66e) / [7008](weapon:mod_custom)";
            const string second = "[RIM-67B](weapon:usn_rim-67b) / [RIM-66E](weapon:usn_rim-66e) / 7008";
            chat.TryAdd("First", first, out _); chat.TryAdd("Second", second, out _);
            var weapons = new AdvisorWeaponLinks(key => key is "usn_rim-66e" or "usn_rim-67b" or "mod_custom");
            var units = new AdvisorUnitLinks(id => id == 7008);
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty",
                links: units, weaponLinks: weapons)).Root!;
            Require(weapons.Links.Select(link => link.ReferenceKey).SequenceEqual(new[] { "usn_rim-66e", "mod_custom", "usn_rim-67b", "usn_rim-66e" }) &&
                units.Links.Count == 1 && units.Links[0].UnitId == 7008, "A weapon key changed between replies or its label became a unit action");
            var firstLink = rendered.Descendants(Ns + "Hyperlink").First();
            Require(firstLink.Descendants(Ns + "Span").Any(span => (string?)span.Attribute("FontWeight") == "Bold") &&
                !rendered.Descendants(Ns + "Hyperlink").Any(link => link.Descendants(Ns + "Hyperlink").Any()), "Weapon formatting or link boundaries changed");
            Require(!Visible(rendered).Contains("weapon:") && chat.Exchanges[0].Answer == first && chat.Exchanges[1].Answer == second,
                "Rendering exposed a key or changed the text used for copying and AI history");
        });
        check("Unavailable weapon references remain plain labels without unit fallback, and literal or external links stay unchanged", () =>
        {
            var chat = new AdvisorConversation();
            const string answer = "[7008](weapon:missing) / [7008](weapon:) / [7008](weapon:weapon_reference_7) / 7008";
            chat.TryAdd("Assess", answer, out _);
            foreach (var enabled in new[] { false, true })
            {
                var units = new AdvisorUnitLinks(id => id == 7008);
                var weapons = enabled ? new AdvisorWeaponLinks(_ => false) : null;
                var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty",
                    links: units, weaponLinks: weapons)).Root!;
                Require(units.Links.Count == 1 && !Visible(rendered).Contains("weapon:") &&
                    Visible(rendered).Contains("7008 / 7008 / 7008 / 7008"), "An unavailable weapon was rebound to a unit or leaked its destination");
            }
            var plain = AdvisorMarkdown.Render("`[7008](weapon:mod_custom)` / [site](https://example.invalid) / ![image](weapon:mod_custom)",
                weaponLink: new AdvisorWeaponLinks(_ => true).Reference);
            Require(!plain.Descendants(Ns + "Hyperlink").Any() && Visible(plain).Contains("[7008](weapon:mod_custom)") &&
                Visible(plain).Contains("https://example.invalid"), "Literal text, an image or an external link acquired a weapon action");
        });
        check("Unlinked weapon mentions reuse the first valid link in their own answer while preserving text and formatting", () =>
        {
            var chat = new AdvisorConversation();
            const string first = "RGM-84C / [**RGM-84C**](weapon:harpoon) / **RGM-84C** / [RGM-84C](weapon:other) / RGM-84C / RGM-84C2 / RGM-84C-ER / [Mk-48 Mod 4](weapon:mk48) / Mk-48 Mod 4";
            const string second = "RGM-84C / Mk-48 Mod 4";
            chat.TryAdd("RGM-84C", first, out _); chat.TryAdd("RGM-84C", second, out _);
            var weapons = new AdvisorWeaponLinks(key => key is "harpoon" or "other" or "mk48");
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty", weaponLinks: weapons)).Root!;
            var targets = weapons.Links.ToDictionary(link => link.Name, link => link.ReferenceKey);
            var anchors = rendered.Descendants(Ns + "Hyperlink").ToArray();
            var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
            Require(anchors.Select(link => targets[(string)link.Attribute(x + "Name")!]).SequenceEqual(new[]
                { "harpoon", "harpoon", "harpoon", "other", "harpoon", "mk48", "mk48" }),
                "A repeated weapon used the wrong target, or completion affected another answer, a question or a longer weapon name");
            Require(anchors.Any(link => Visible(link) == "RGM-84C" && link.Ancestors(Ns + "Span").Any(span => (string?)span.Attribute("FontWeight") == "Bold")) &&
                !anchors.Any(link => link.Descendants(Ns + "Hyperlink").Any()), "Completion removed emphasis or nested an existing link");
            Require(chat.Exchanges[0].Answer == first && chat.Exchanges[1].Answer == second &&
                Visible(rendered).Contains("RGM-84C / RGM-84C / RGM-84C / RGM-84C / RGM-84C / RGM-84C2 / RGM-84C-ER / Mk-48 Mod 4 / Mk-48 Mod 4"),
                "Completion changed displayed text or the source used for copying and AI history");
        });
        check("Weapon completion leaves code, external references and explicit destinations untouched", () =>
        {
            const string answer = "[RGM-84C](weapon:missing) / [RGM-84C](weapon:harpoon) / `RGM-84C` / [RGM-84C](https://example.invalid/RGM-84C) / ![RGM-84C](https://example.invalid/RGM-84C) / <https://example.invalid/RGM-84C> / [RGM-84C](unit:missing) / RGM-84C\n\n```text\nRGM-84C\n```";
            var chat = new AdvisorConversation(); chat.TryAdd("Assess", answer, out _);
            var weapons = new AdvisorWeaponLinks(key => key == "harpoon");
            var rendered = XDocument.Parse(AdvisorTranscript.Render(chat, AdvisorText.Load("en"), "Michael Reed", "empty", weaponLinks: weapons)).Root!;
            Require(weapons.Links.Count == 2 && weapons.Links.All(link => link.ReferenceKey == "harpoon") &&
                rendered.Descendants(Ns + "Hyperlink").Count() == 2, "Completion modified literal content or replaced an explicit destination");
            Require(Visible(rendered).Contains("https://example.invalid/RGM-84C") && chat.Exchanges[0].Answer == answer,
                "Completion rewrote an external URL or the original answer");
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
                var result = (XElement)method.Invoke(null, new object[] { "# Ready\n\n**Bundled** parser.", null!, null! })!;
                Require(Visible(result) == "ReadyBundled parser.", "The distributed parser could not render the actual advisor code");
            }
            finally { context.Unload(); }
        });
    }

    private static string Visible(XElement element) => string.Concat(element.DescendantsAndSelf().Attributes("Text").Select(a => Literal(a.Value)));
    private static string Literal(string value) => value.StartsWith("{}", StringComparison.Ordinal) ? value.Substring(2) : value;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
