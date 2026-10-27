using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor.Api;

namespace SeaPowerAIAdvisor;

// Implemented by the SDK bridge, never by the dispatcher interpreting a command's argument JSON.
internal interface IAdvisorReservationCommand
{
    bool ComposesReservations { get; }
    bool EndsReservationSequence { get; }
    ReservationQueueState InspectQueue(string target, ReservationPlacement placement);
    bool BeginQueue(string target, ReservationPlacement placement);
    void ValidateReservations(IReadOnlyList<ReservationOrder> orders);
    string DescribeReservation(ReservationOrder order, string language);
    ReservationPreview PreviewReservation(ReservationOrder order, string language);
    JObject ExecuteReservation(ReservationOrder order);
}

internal sealed partial class AdvisorOperations
{
    private IAdvisorReservationCommand? ReservationCommand(AdvisorOperationOrder order) =>
        Operation(order.OperationId) is IAdvisorReservationCommand command && command.ComposesReservations ? command : null;

    private sealed class ReservationPlan
    {
        internal readonly Dictionary<string, ReservationPlacement> Placements;
        internal readonly Dictionary<string, ReservationQueueState> Queues = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ReservationOrder> Steps = new(StringComparer.Ordinal);
        internal ReservationPlan(Dictionary<string, ReservationPlacement> placements) => Placements = placements;
    }
    private ReservationPlan ValidatePlan(AdvisorOperationPlan plan)
    {
        if (plan.ImmediateOrders.Any(o => Operation(o.OperationId).ExecutionMode != OperationExecutionMode.Immediate) ||
            plan.ReservationOrders.Any(o => Operation(o.OperationId).ExecutionMode != OperationExecutionMode.Reservation))
            throw new AdvisorOperationException(400, "operation_phase_mismatch");
        var placements = ReadPlacements(plan.ReservationUpdates);
        var result = new ReservationPlan(placements);
        var targets = new HashSet<string>(plan.ReservationOrders.Where(o => ReservationCommand(o) != null).Select(o => o.Target), StringComparer.Ordinal);
        if (!targets.SetEquals(placements.Keys)) throw new AdvisorOperationException(400,
            targets.Count > 0 && placements.Count == 0 ? "missing_reservation_updates" : "reservation_policy_mismatch");
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var ended = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in plan.ReservationOrders)
        {
            var command = ReservationCommand(order);
            if (command == null)
            {
                if (targets.Contains(order.Target)) throw new AdvisorOperationException(400, "operation_not_composable");
                continue;
            }
            if (ended.Contains(order.Target)) throw new AdvisorOperationException(400, "reservation_after_terminal_operation");
            if (!result.Queues.TryGetValue(order.Target, out var queue))
            {
                queue = command.InspectQueue(order.Target, placements[order.Target]);
                if (queue.UnavailableReason is string reason) throw new AdvisorOperationException(409, reason);
                result.Queues.Add(order.Target, queue);
            }
            indexes.TryGetValue(order.Target, out var index); indexes[order.Target] = index + 1;
            var start = placements[order.Target] == ReservationPlacement.Replace || queue.BecomesIndependent
                ? queue.CurrentPosition : queue.EndPosition ?? queue.CurrentPosition;
            result.Steps.Add(order.Id, new ReservationOrder(new OperationOrder(order.Target, order.Arguments.ToString(Newtonsoft.Json.Formatting.None)),
                placements[order.Target], queue, start, index));
            if (command.EndsReservationSequence) ended.Add(order.Target);
        }
        foreach (var group in plan.Orders.GroupBy(o => o.OperationId))
        {
            var command = ReservationCommand(group.First());
            if (command != null) command.ValidateReservations(group.Select(o => result.Steps[o.Id]).ToArray());
            else Operation(group.Key).Validate(group.ToArray());
        }
        return result;
    }
    private static Dictionary<string, ReservationPlacement> ReadPlacements(JArray updates)
    {
        var result = new Dictionary<string, ReservationPlacement>(StringComparer.Ordinal);
        foreach (var value in updates)
        {
            if (value is not JObject item || item.Count != 2 || item["target_ref"]?.Type != JTokenType.String ||
                string.IsNullOrWhiteSpace((string?)item["target_ref"]) || item["placement"]?.Type != JTokenType.String ||
                (string?)item["placement"] is not ("append" or "replace"))
                throw new AdvisorOperationException(400, "invalid_reservation_policy");
            if (!result.TryAdd((string)item["target_ref"]!, (string)item["placement"]! == "replace" ? ReservationPlacement.Replace : ReservationPlacement.Append))
                throw new AdvisorOperationException(400, "duplicate_reservation_policy");
        }
        return result;
    }
    private string DescribePlan(AdvisorOperationPlan plan, string language)
    {
        var prepared = ValidatePlan(plan); var text = AdvisorText.Load(language); var sections = new List<string>();
        if (plan.ImmediateOrders.Count > 0)
            sections.Add(text["operation.immediate_heading"] + "\n" + string.Join("\n\n", plan.ImmediateOrders.GroupBy(o => o.OperationId)
                .Select(g => Operation(g.Key).DescribeProposal(g.ToArray(), language))));
        if (plan.ReservationOrders.Count > 0)
        {
            var descriptions = prepared.Steps.Count == 0
                ? plan.ReservationOrders.GroupBy(o => o.OperationId).Select(g => Operation(g.Key).DescribeProposal(g.ToArray(), language))
                : plan.ReservationOrders.Select(order => ReservationCommand(order) is IAdvisorReservationCommand command
                    ? command.DescribeReservation(prepared.Steps[order.Id], language)
                    : Operation(order.OperationId).DescribeProposal(new[] { order }, language));
            sections.Add(text["operation.reservation_heading"] + "\n" + string.Join("\n\n", descriptions));
        }
        return string.Join("\n\n", sections);
    }
    private AdvisorMapPreview PreviewPlan(AdvisorOperationPlan plan, string language)
    {
        var prepared = ValidatePlan(plan);
        var tails = new Dictionary<string, AdvisorMapCoordinate?>(StringComparer.Ordinal);
        var drawings = new List<AdvisorMapDrawing>();
        foreach (var group in plan.ImmediateOrders.GroupBy(o => o.OperationId))
            if (Operation(group.Key) is IAdvisorOperationPreview preview) drawings.AddRange(preview.Preview(group.ToArray(), language).Drawings);
        if (prepared.Steps.Count == 0)
        {
            foreach (var group in plan.ReservationOrders.GroupBy(o => o.OperationId))
                if (Operation(group.Key) is IAdvisorOperationPreview preview) drawings.AddRange(preview.Preview(group.ToArray(), language).Drawings);
            return new AdvisorMapPreview(drawings);
        }
        foreach (var order in plan.ReservationOrders)
        {
            if (ReservationCommand(order) is IAdvisorReservationCommand command)
            {
                var step = prepared.Steps[order.Id];
                var start = tails.TryGetValue(order.Target, out var tail) ? tail : step.StartPosition;
                var preview = command.PreviewReservation(new ReservationOrder(step.Order, step.Placement, step.Queue, start, step.Index), language);
                drawings.AddRange(preview.Preview.Drawings); tails[order.Target] = preview.EndPosition;
            }
            else if (Operation(order.OperationId) is IAdvisorOperationPreview preview)
                drawings.AddRange(preview.Preview(new[] { order }, language).Drawings);
        }
        return new AdvisorMapPreview(drawings);
    }
    private static AdvisorOperationPlan CombinePlans(IEnumerable<AdvisorOperationPlan> selected)
    {
        var plans = selected.ToArray(); var updates = new JArray(); var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in plans.SelectMany(p => p.ReservationUpdates).Cast<JObject>())
        {
            var target = (string)value["target_ref"]!; var placement = (string)value["placement"]!;
            if (seen.TryGetValue(target, out var previous))
            {
                if (placement == "replace" || previous == "replace") throw new AdvisorOperationException(400, "conflicting_selected_reservations");
                continue;
            }
            seen.Add(target, placement); updates.Add(value.DeepClone());
        }
        return new AdvisorOperationPlan("selected", "", "", plans.SelectMany(p => p.ImmediateOrders), plans.SelectMany(p => p.ReservationOrders), updates);
    }
}
