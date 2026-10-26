using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using static SeaPowerAIAdvisor.AdvisorMarkup;

namespace SeaPowerAIAdvisor;

internal static class AdvisorTranscript
{
    internal static string Render(AdvisorConversation conversation, AdvisorText text, string advisor, string empty,
        AdvisorNotice? configurationNotice = null, AdvisorTextSize textSize = AdvisorTypography.DefaultSize, AdvisorUnitLinks? links = null,
        AdvisorWeaponLinks? weaponLinks = null, AdvisorCoordinateLinks? coordinateLinks = null)
    {
        var root = Element("StackPanel", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"));
        var busy = conversation.Turns.Any(t => t.State == AdvisorTurnState.Waiting);
        if (configurationNotice != null) root.Add(Error(configurationNotice.Message(text)));
        if (conversation.Turns.Count == 0 && configurationNotice == null)
        {
            root.Add(Plain(empty));
            if (links != null || coordinateLinks != null) root.Add(AdvisorMarkdown.Render(text["transcript.track_navigation"]));
        }
        for (var i = 0; i < conversation.Turns.Count; i++)
        {
            var turn = conversation.Turns[i];
            var copyCaption = turn.Request.IsShortcut ? "action.copy_prompt" : "action.copy";
            var copyTip = turn.Request.IsShortcut ? "transcript.copy_prompt_tip" : "transcript.copy_question_tip";
            var row = Element("StackPanel", new XAttribute("Margin", "0,0,0,20"),
                Header(text["speaker.you"], "AdvisorPlayerNameBrush", "CopyQuestion" + i, text, copyTip, "0,0,0,6", copyCaption));
            if (turn.Request.Units.Count > 0 || turn.Request.Locations.Count > 0)
                row.Add(Element("WrapPanel", new XAttribute("Margin", "0,0,0,6"),
                    turn.Request.Units.Select(unit => AdvisorBadge.Render(unit.Id, unit.Name, links))
                        .Concat(turn.Request.Locations.Select(location => AdvisorBadge.Render(location, coordinateLinks)))));
            if (turn.Request.DisplayBody.Length > 0)
                row.Add(links?.Decorate(Plain(turn.Request.DisplayBody)) ?? Plain(turn.Request.DisplayBody));
            if (turn.State == AdvisorTurnState.Waiting || turn.State == AdvisorTurnState.Answered)
            {
                var header = Header(turn.AdvisorName.Length == 0 ? advisor : turn.AdvisorName, "AdvisorNameBrush",
                    turn.State == AdvisorTurnState.Answered ? "CopyAnswer" + i : null, text, "transcript.copy_tip", "0,6,0,6", elapsed: turn.Elapsed);
                header.Add(new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", "AnswerHeader" + i));
                row.Add(header);
            }
            if (turn.State == AdvisorTurnState.Answered)
            {
                var exchange = turn.Exchange ?? throw new InvalidOperationException("The completed answer is missing.");
                var answer = AdvisorMarkdown.Render(exchange.Answer, links == null ? null :
                    (id, label) => links.Reference(id, label, turn.UnitReferences),
                    weaponLinks == null ? null : weaponLinks.Reference,
                    coordinateLinks == null ? null : coordinateLinks.Reference);
                weaponLinks?.Decorate(answer);
                row.Add(links?.Decorate(answer) ?? answer);
                if (BattleContactAssessment.CanReassess(turn))
                {
                    var actions = Element("WrapPanel", new XAttribute("Margin", "0,6,0,0"));
                    foreach (var assumption in new[] { ContactAssumption.Friendly, ContactAssumption.Enemy })
                    {
                        var button = Action("Reassess" + assumption + i,
                            text["contact.reassess_" + BattleContactAssessment.AffiliationCode(assumption)], null);
                        button.Add(new XAttribute("Margin", "0,0,6,6"), new XAttribute("IsEnabled", !busy));
                        actions.Add(button);
                    }
                    row.Add(actions);
                }
            }
            else if (turn.State == AdvisorTurnState.Failed)
                row.Add(Error((turn.Error ?? throw new InvalidOperationException("The failed turn has no error.")).Message(text)));
            else if (turn.State == AdvisorTurnState.Waiting)
            {
                var waiting = Plain(AdvisorProgress.Format(turn.Progress, text));
                waiting.Add(new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", "ProgressTurn" + i));
                row.Add(waiting);
            }
            else if (turn.State == AdvisorTurnState.Incomplete)
            {
                var notice = text[turn.CompletionReason == AdvisorCompletionReason.PayloadLimit
                    ? "transcript.payload_limit" : "transcript.round_limit"];
                if (turn.CompletionReason == AdvisorCompletionReason.RoundLimit &&
                    turn.Progress is { MaxRounds: var maxRounds } && maxRounds < AdvisorQueryLimits.DefaultMaxRounds)
                    notice += text.Format("transcript.round_limit_recommendation", AdvisorQueryLimits.DefaultMaxRounds);
                row.Add(Plain(notice));
                if (turn.Proposal?.Orders.Count > 0) row.Add(Plain(text["transcript.proposal_expired"]));
            }
            else row.Add(Plain(text["status.cancelled"]));
            if (turn.State != AdvisorTurnState.Incomplete && turn.Proposal is AdvisorOperationProposal proposal && proposal.Orders.Count > 0 && proposal.State != AdvisorProposalState.Collecting)
            {
                var actions = Element("WrapPanel", new XAttribute("Margin", "0,6,0,0"));
                foreach (var action in ReviewActions(proposal, i, text))
                {
                    var review = Action(action.Name, action.Caption, null);
                    review.Add(new XAttribute("Margin", "0,0,6,6"), new XAttribute("IsEnabled", !busy && proposal.State == AdvisorProposalState.Pending));
                    if (action.PlanId != null)
                    {
                        review.Attribute("Content")!.Remove();
                        review.Add(Element("TextBlock", Text("Text", action.Caption), new XAttribute("MaxWidth", "280"),
                            new XAttribute("TextTrimming", "CharacterEllipsis")));
                    }
                    actions.Add(review);
                }
                if (proposal.State is AdvisorProposalState.Registered or AdvisorProposalState.Partial or AdvisorProposalState.Rejected or AdvisorProposalState.Unknown)
                    for (var planIndex = 0; planIndex < proposal.Plans.Count; planIndex++)
                    {
                        var plan = proposal.Plans[planIndex];
                        if (plan.Title.Length == 0 || !plan.Orders.Any(proposal.ExecutionOrders.Contains)) continue;
                        actions.Add(Element("TextBlock", Text("Text", text.Format("operation.review_plan", planIndex + 1, plan.Title)),
                            new XAttribute("FontSize", "12"), new XAttribute("TextWrapping", "Wrap"),
                            new XAttribute("MaxWidth", "280"), new XAttribute("VerticalAlignment", "Center"), new XAttribute("Margin", "0,0,6,6")));
                    }
                row.Add(actions);
                if (proposal.ErrorCode != null)
                    row.Add(Error(text.Format("operation.failure", proposal.ErrorCode, proposal.RegisteredCount,
                        proposal.ExecutionOrders.Count == 0 ? proposal.Orders.Count : proposal.ExecutionOrders.Count)));
            }
            if (conversation.CanRetry(turn)) row.Add(Action("RetryTurn" + i, text["action.retry"], text["transcript.retry_tip"]));
            root.Add(row);
        }
        AdvisorTypography.Apply(root, textSize);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    // Rendering and event binding share the same button identities and retained plan IDs.
    internal static IEnumerable<(string Name, string Caption, string? PlanId)> ReviewActions(
        AdvisorOperationProposal proposal, int turnIndex, AdvisorText text)
    {
        var name = "ReviewProposal" + turnIndex;
        if (proposal.Plans.Count == 1 || proposal.State != AdvisorProposalState.Pending)
        {
            yield return (name, text[proposal.State == AdvisorProposalState.Pending ? "operation.review" : "operation.accept"], null);
            yield break;
        }
        for (var i = 0; i < proposal.Plans.Count; i++)
            yield return (name + "Plan" + i, text.Format("operation.review_plan", i + 1, proposal.Plans[i].Title), proposal.Plans[i].Id);
    }

    private static XElement Header(string speaker, string brush, string? copyName, AdvisorText text, string tipKey, string margin,
        string captionKey = "action.copy", TimeSpan? elapsed = null)
    {
        var header = Element("Grid", new XAttribute("Margin", margin),
            Element("Grid.ColumnDefinitions", Element("ColumnDefinition", new XAttribute("Width", "*")),
                Element("ColumnDefinition", new XAttribute("Width", "Auto")),
                Element("ColumnDefinition", new XAttribute("Width", "Auto"))), Label(speaker, brush));
        if (elapsed.HasValue)
            header.Add(Element("TextBlock",
                Text("Text", ((long)elapsed.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"),
                new XAttribute("FontSize", "12"), new XAttribute("Foreground", "#8996A3"), new XAttribute("Grid.Column", "1"),
                new XAttribute("VerticalAlignment", "Center"), new XAttribute("Margin", "8,0,8,0")));
        if (copyName != null) header.Add(Action(copyName, text[captionKey], text[tipKey], 2));
        return header;
    }

    private static XElement Action(string name, string caption, string? tip, int column = 0)
    {
        var button = Element("Button", new XAttribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name", name),
            new XAttribute("Grid.Column", column), new XAttribute("FontSize", "12"), new XAttribute("Padding", "8,2"),
            new XAttribute("Focusable", "False"), new XAttribute("HorizontalAlignment", "Left"), Text("Content", caption));
        if (tip != null) button.Add(Text("ToolTip", tip));
        return button;
    }
    private static XElement Error(string value)
    {
        var element = Plain(value);
        element.Add(new XAttribute("Foreground", "{DynamicResource AdvisorErrorBrush}"));
        return element;
    }
    private static XElement Label(string value, string brush) => Element("TextBlock", Text("Text", value),
        new XAttribute("Foreground", "{DynamicResource " + brush + "}"),
        new XAttribute("FontWeight", "Bold"), new XAttribute("FontSize", "14"),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("Margin", "0,0,0,5"));
    private static XElement Plain(string value) => Element("TextBlock", Text("Text", value),
        new XAttribute("TextWrapping", "Wrap"), new XAttribute("FontSize", "14"), new XAttribute("Margin", "0,0,0,8"));
}
