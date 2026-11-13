using System.Threading;
using System.Threading.Tasks;
using Content.Server.Fluids.EntitySystems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Tag;
using Content.Shared.Timing.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Interactions;

public sealed partial class MopPuddleOperator : HTNOperator
{
    private static readonly ProtoId<TagPrototype> MopTag = "Mop";

    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private PuddleSystem _puddles = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private UseDelaySystem _useDelay = default!;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard, CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return Task.FromResult<(bool, Dictionary<string, object>?)>((CanMop(owner), null));
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var target = blackboard.GetValue<EntityUid>("Target");
        var dirt = GetDirtVolume(target);

        if (dirt == FixedPoint2.Zero)
            return HTNOperatorStatus.Finished;

        if (!CanMop(owner) || !_entities.TryGetComponent<TransformComponent>(target, out var xform) ||
            !_interaction.InRangeUnobstructed(owner, target))
            return HTNOperatorStatus.Failed;

        _interaction.UserInteraction(owner, xform.Coordinates, target);
        return GetDirtVolume(target) < dirt ? HTNOperatorStatus.Finished : HTNOperatorStatus.Failed;
    }

    private bool CanMop(EntityUid owner)
    {
        if (!_hands.TryGetActiveItem(owner, out var held) ||
            !_tags.HasTag(held.Value, MopTag) ||
            !_entities.TryGetComponent<AbsorbentComponent>(held, out var absorber) ||
            _useDelay.IsDelayed(held.Value) ||
            !_solutions.TryGetSolution(held.Value, absorber.SolutionName, out _, out var solution))
            return false;

        return absorber.UseAbsorberSolution
            ? solution.GetTotalPrototypeQuantity(_puddles.GetAbsorbentReagents(solution)) > FixedPoint2.Zero
            : solution.AvailableVolume > FixedPoint2.Zero;
    }

    private FixedPoint2 GetDirtVolume(EntityUid target)
    {
        if (_entities.IsQueuedForDeletion(target) ||
            !_entities.TryGetComponent<PuddleComponent>(target, out var puddle) ||
            !_solutions.TryGetSolution(target, puddle.SolutionName, out _, out var solution))
            return FixedPoint2.Zero;

        return solution.Volume - solution.GetTotalPrototypeQuantity(_puddles.GetAbsorbentReagents(solution));
    }
}
