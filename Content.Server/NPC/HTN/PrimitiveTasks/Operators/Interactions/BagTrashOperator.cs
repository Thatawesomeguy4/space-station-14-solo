using Content.Server.NPC.HTN.PrimitiveTasks.Operators.Specific;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Robust.Shared.Containers;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Interactions;

public sealed partial class BagTrashOperator : HTNOperator
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private TagSystem _tags = default!;

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        var target = blackboard.GetValue<EntityUid>("Target");
        var bag = blackboard.GetValue<EntityUid>(PickNearbyTrashOperator.TrashBagKey);

        if (!_hands.IsHolding(owner, bag) ||
            !_entities.TryGetComponent<StorageComponent>(bag, out var storage) ||
            !_entities.TryGetComponent<TransformComponent>(target, out var transform) ||
            !_tags.HasTag(target, PickNearbyTrashOperator.TrashTag) ||
            _containers.IsEntityInContainer(target) ||
            !_interaction.IsAccessible(owner, target) ||
            !_interaction.InRangeUnobstructed(owner, target) ||
            !_storage.CanInsert(bag, target, out _, storage))
            return HTNOperatorStatus.Failed;

        _interaction.InteractUsing(owner, bag, target, transform.Coordinates);
        return storage.Container.Contains(target) || _entities.IsQueuedForDeletion(target)
            ? HTNOperatorStatus.Finished
            : HTNOperatorStatus.Failed;
    }
}
