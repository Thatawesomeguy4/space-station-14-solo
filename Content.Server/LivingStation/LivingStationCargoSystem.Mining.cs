using System.Linq;
using Content.Server.Lathe;
using Content.Server.Materials;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Lock;
using Content.Shared.Materials;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Content.Shared.Tag;
using Content.Shared.Tools.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationCargoSystem
{
    private static readonly ProtoId<TagPrototype> OreTag = "Ore";
    private static readonly ProtoId<StackPrototype> Glass = "Glass";
    private static readonly ProtoId<LatheRecipePackPrototype> Smelting = "OreSmelting";
    private const int MaterialDeliverySize = 10;

    [Dependency] private LatheSystem _lathe = default!;
    [Dependency] private MaterialStorageSystem _materials = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private LockSystem _locks = default!;
    [Dependency] private WeldableSystem _welds = default!;

    private EntityUid? FindOreProcessor(EntityUid grid)
    {
        var processors = EntityQueryEnumerator<CargoOreProcessorComponent, TransformComponent>();
        while (processors.MoveNext(out var uid, out _, out var transform))
        {
            if (transform.GridUid == grid && transform.Anchored)
                return uid;
        }

        return null;
    }

    private bool TryDispatchOre(CargoDeliveryPickupComponent route, EntityUid grid, MapGridComponent mapGrid, EntityUid worker)
    {
        if (_timing.CurTime < route.NextOreHaul ||
            FindDepartmentPallet(grid, CargoDepartment.OreIntake) is not { } intake ||
            FindDepartmentPallet(grid, CargoDepartment.OreProcessing) is not { } dropoff ||
            FindOreProcessor(grid) == null || !IsDropoffClear(dropoff, grid, mapGrid))
            return false;

        foreach (var crate in CratesOnPallet(Transform(intake), grid, mapGrid))
        {
            if (!CanHaul(crate) || _locks.IsLocked(crate) || _welds.IsWelded(crate) ||
                !Comp<EntityStorageComponent>(crate).Contents.ContainedEntities.Any(IsProcessableOre))
                continue;

            route.NextOreHaul = _timing.CurTime + TimeSpan.FromSeconds(15);
            return TryAssignDelivery(worker, crate, dropoff);
        }

        return false;
    }

    private bool IsProcessableOre(EntityUid item) => !TerminatingOrDeleted(item) && !EntityManager.IsQueuedForDeletion(item) &&
        _tags.HasTag(item, OreTag) && (!TryComp(item, out StackComponent? stack) || !stack.Unlimited && stack.Count > 0);

    private void ProcessStagedOre(CargoDeliveryPickupComponent route, EntityUid grid, MapGridComponent mapGrid, EntityUid worker)
    {
        route.OreIssue = null;
        if (FindDepartmentPallet(grid, CargoDepartment.OreProcessing) is not { } pallet ||
            FindDepartmentPallet(grid, CargoDepartment.Materials) is not { } output ||
            FindOreProcessor(grid) is not { } processor || !TryComp(processor, out LatheComponent? lathe) ||
            !Transform(processor).Coordinates.TryDistance(EntityManager, Transform(pallet).Coordinates, out var distance) || distance > 2f)
            return;

        var crates = CratesOnPallet(Transform(pallet), grid, mapGrid).Where(CanHaul).ToArray();
        if (crates.Length == 0 && !route.OreLoaded)
            return;

        if (!_power.IsPowered(processor))
        {
            route.OreIssue = "living-station-cargo-ore-unpowered";
            return;
        }

        // The normal lathe queue belongs to whoever is already using the machine.
        if (lathe.CurrentRecipe != null || lathe.Queue.Count != 0)
        {
            route.OreIssue = "living-station-cargo-ore-busy";
            return;
        }

        foreach (var crate in crates)
        {
            if (_locks.IsLocked(crate) || _welds.IsWelded(crate))
                continue;

            foreach (var item in Comp<EntityStorageComponent>(crate).Contents.ContainedEntities.ToArray())
            {
                if (!IsProcessableOre(item))
                    continue;
                if (_materials.TryInsertMaterialEntity(worker, item, processor))
                    route.OreLoaded = true;
                else
                    route.OreIssue = "living-station-cargo-ore-rejected";
            }
        }

        if (!route.OreLoaded)
            return;

        // The basic smelting recipes complete immediately. Capture just this run's output before it can merge.
        _oreOutput = (processor, Transform(output).Coordinates);
        try
        {
            foreach (var id in ProtoMan.Index(Smelting).Recipes)
            {
                var recipe = ProtoMan.Index(id);
                var quantity = SharedLatheSystem.MaxItemsPerRequest;
                foreach (var (material, amount) in recipe.Materials)
                {
                    var cost = recipe.ApplyMaterialDiscount ? (int) (amount * lathe.MaterialUseMultiplier) : amount;
                    if (cost > 0)
                        quantity = Math.Min(quantity, _materials.GetMaterialAmount(processor, material.Id) / cost);
                }

                // Limit each synchronous batch so the lathe's instant-completion calls stay shallow.
                if (recipe.CompleteTime == TimeSpan.Zero && _lathe.TryAddToQueue(processor, recipe, Math.Min(quantity, 30), lathe))
                    _lathe.TryStartProducing(processor, lathe);
            }
        }
        finally
        {
            _oreOutput = null;
        }

        if (_materials.GetTotalMaterialAmount(processor) > 0)
            route.OreIssue ??= "living-station-cargo-ore-buffered";
        else
            route.OreLoaded = false;
    }

    private (EntityUid Processor, EntityCoordinates Pallet)? _oreOutput;

    private void OnOrePrinted(Entity<CargoOreProcessorComponent> processor, ref LatheItemPrintedEvent args)
    {
        if (_oreOutput is { } output && output.Processor == processor.Owner)
            _transform.SetCoordinates(args.Item, output.Pallet);
    }

    private static ProtoId<StackPrototype>? GetRequestedMaterial(EntProtoId product) =>
        product == MinedSteel ? Steel : product == MinedGlass ? Glass : (ProtoId<StackPrototype>?) null;

    private List<Entity<StackComponent>> FindRefinedStacks(EntityUid grid, ProtoId<StackPrototype> material)
    {
        if (FindDepartmentPallet(grid, CargoDepartment.Materials) is not { } pallet || !TryComp(grid, out MapGridComponent? mapGrid))
            return [];

        var position = Transform(pallet).Coordinates;
        var tile = _map.TileIndicesFor(grid, mapGrid, position);
        return _lookup.GetEntitiesInRange<StackComponent>(position, 0.71f, LookupFlags.Uncontained)
            .Where(stack => stack.Comp.StackTypeId == material && !stack.Comp.Unlimited && stack.Comp.Count > 0 &&
                            !EntityManager.IsQueuedForDeletion(stack) && Transform(stack).ParentUid == grid &&
                            _map.TileIndicesFor(grid, mapGrid, Transform(stack).Coordinates) == tile)
            .OrderBy(stack => stack.Owner).ToList();
    }

    private void PackRefinedMaterials(List<Entity<StackComponent>> supplies, EntityUid crate, EntityCoordinates position)
    {
        var remaining = MaterialDeliverySize;
        foreach (var stack in supplies)
        {
            var amount = Math.Min(remaining, stack.Comp.Count);
            var item = amount == stack.Comp.Count ? stack.Owner : _stacks.Split(stack.AsNullable(), amount, position);
            if (item is not { } packed || !_storage.Insert(packed, crate))
                break;

            remaining -= amount;
            if (remaining == 0)
                break;
        }
    }
}
