using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators.Specific;

public sealed partial class PickNearbyTrashOperator : HTNOperator
{
    internal const string TrashBagKey = "TrashBag";
    internal static readonly ProtoId<TagPrototype> TrashBagTag = "TrashBag";
    internal static readonly ProtoId<TagPrototype> TrashTag = "Trash";

    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private TagSystem _tags = default!;

    public override Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(
        NPCBlackboard blackboard, CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        foreach (var bag in _hands.EnumerateHeld(owner))
        {
            if (!_tags.HasTag(bag, TrashBagTag))
                continue;

            var target = FindTrash(owner, bag, blackboard);
            if (target.IsValid())
            {
                return Task.FromResult<(bool, Dictionary<string, object>?)>((true, new()
                {
                    ["Target"] = target,
                    ["TargetCoordinates"] = new EntityCoordinates(target, Vector2.Zero),
                    [TrashBagKey] = bag,
                }));
            }
        }

        return Task.FromResult<(bool, Dictionary<string, object>?)>((false, null));
    }

    private EntityUid FindTrash(EntityUid owner, EntityUid bag, NPCBlackboard blackboard)
    {
        var origin = _entities.GetComponent<TransformComponent>(owner).Coordinates;
        var radius = blackboard.GetValueOrDefault<float>(blackboard.GetVisionRadiusKey(_entities), _entities);
        var nearest = EntityUid.Invalid;
        var nearestDistance = radius;

        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(origin, radius, LookupFlags.Uncontained))
        {
            var target = item.Owner;
            if (!_tags.HasTag(target, TrashTag) || _entities.IsQueuedForDeletion(target))
                continue;

            var coordinates = _entities.GetComponent<TransformComponent>(target).Coordinates;
            if (!origin.TryDistance(_entities, coordinates, out var distance) || distance >= nearestDistance ||
                !_examine.InRangeUnOccluded(owner, target, radius) ||
                !_storage.CanInsert(bag, target, out _, item: item.Comp))
                continue;

            nearest = target;
            nearestDistance = distance;
        }

        return nearest;
    }
}
