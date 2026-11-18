using System.Linq;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Monitor.Components;
using Content.Server.Doors.Systems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.APC;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Atmos.EntitySystems;
using Content.Shared.Atmos.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Light.Components;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Power;
using Content.Shared.Power.Components;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Stacks;
using Content.Shared.Storage.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tiles;
using Content.Shared.Tools.Components;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationEngineeringSystem
{
    private static readonly ProtoId<StackPrototype> HullTile = "FloorTileSteel";
    private static readonly ProtoId<StackPrototype> LowVoltageCable = "Cable";
    private static readonly ProtoId<ContentTileDefinition> Plating = "Plating";
    private static readonly ProtoId<RadioChannelPrototype> EngineeringChannel = "Engineering";
    private string? _lastWorkResult;

    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private BarotraumaSystem _pressure = default!;
    [Dependency] private SharedInternalsSystem _internals = default!;
    [Dependency] private SharedGasTankSystem _gasTanks = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private FirelockSystem _firelocks = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private CableSystem _cables = default!;
    [Dependency] private FloorTileSystem _floors = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private LivingStationCargoSystem _cargo = default!;
    [Dependency] private SharedEntityStorageSystem _serviceStorage = default!;
    [Dependency] private SharedStorageSystem _packStorage = default!;
    [Dependency] private InventorySystem _inventory = default!;

    private void InitializeInfrastructure()
    {
        SubscribeLocalEvent<EngineeringServicePointComponent, GetVerbsEvent<Verb>>(OnGetInfrastructureVerbs);
        SubscribeLocalEvent<LivingStationEngineerComponent, GetVerbsEvent<Verb>>(OnGetEngineerVerbs);
    }

    private void OnGetInfrastructureVerbs(Entity<EngineeringServicePointComponent> target, ref GetVerbsEvent<Verb> args)
    {
        AddRepairVerb(target, ref args);
        var requester = args.User;
        if (args.CanAccess && args.CanInteract && CanRequestMaintenance(target, requester))
        {
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("living-station-engineering-prioritize"),
                Act = () =>
                {
                    if (CanRequestMaintenance(target, requester))
                        QueueMaintenance(target, requester, urgent: true);
                },
            });
        }
    }

    private void QueueMaintenance(EntityUid target, EntityUid requester, bool urgent)
    {
        var index = _maintenanceRequests.FindIndex(order => order.Target == target);
        var pending = index >= 0;
        if (_engineers.Any(worker => TryComp(worker, out LivingStationWorkClaimComponent? claim) && claim.Target.EntityId == target) ||
            pending && !urgent)
        {
            _popup.PopupEntity(Loc.GetString("living-station-engineering-repair-pending"), target, requester);
            return;
        }

        if (!pending && _maintenanceRequests.Count >= 8)
        {
            _popup.PopupEntity(Loc.GetString("living-station-engineering-repair-full"), target, requester);
            return;
        }

        if (urgent)
        {
            var request = pending ? _maintenanceRequests[index] : (target, requester);
            if (pending)
                _maintenanceRequests.RemoveAt(index);
            _maintenanceRequests.Insert(0, request);
        }
        else
            _maintenanceRequests.Add((target, requester));

        if (!pending)
            RememberRepairRequest(requester, target);

        _retryMaintenance.Remove(target);
        _popup.PopupEntity(Loc.GetString(urgent ? "living-station-engineering-prioritized" :
            "living-station-engineering-repair-queued"), target, requester);
    }

    private void OnGetEngineerVerbs(Entity<LivingStationEngineerComponent> engineer, ref GetVerbsEvent<Verb> args)
    {
        var requester = args.User;
        if (!_engineers.Contains(engineer.Owner) || !args.CanAccess || !args.CanInteract)
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-engineering-check-work"),
            Act = () =>
            {
                if (!_mobState.IsAlive(requester) || !_interaction.InRangeAndAccessible(requester, engineer.Owner) ||
                    !_blocker.CanInteract(requester, engineer))
                    return;

                var active = string.Join(", ", _engineers.Where(uid =>
                    TryComp(uid, out LivingStationWorkClaimComponent? claim) && Exists(claim.Target.EntityId)).Select(uid =>
                    Loc.GetString("living-station-engineering-work-active",
                        ("target", Name(Comp<LivingStationWorkClaimComponent>(uid).Target.EntityId)),
                        ("stage", Loc.GetString(Comp<EngineeringRepairComponent>(uid).Stage)))));
                if (active.Length == 0)
                    active = Loc.GetString("living-station-engineering-work-idle");
                var queued = string.Join(", ", _maintenanceRequests.Where(order => Exists(order.Target)).Select(order => Name(order.Target)));
                if (queued.Length == 0)
                    queued = Loc.GetString("living-station-engineering-work-idle");
                _popup.PopupEntity(Loc.GetString("living-station-engineering-work-status", ("active", active),
                    ("queued", queued), ("last", _lastWorkResult ?? Loc.GetString("living-station-engineering-work-idle"))),
                    engineer, requester);
            },
        });
    }

    private bool NeedsInfrastructureWork(EntityUid target)
    {
        if (!TryComp(target, out EngineeringServicePointComponent? point))
            return false;

        return point.CargoPower && TryComp<ApcPowerReceiverComponent>(target, out var receiver) && receiver.PowerDisabled ||
               point.CableOffset != null && TryComp(target, out ApcComponent? apc) &&
               (!apc.MainBreakerEnabled || IsCableMissing(target, point) || apc.LastExternalState == ApcExternalPowerState.None) ||
               point.HullOffset != null && HasComp<AirAlarmComponent>(target) && IsDepressurized(target);
    }

    private bool IsDepressurized(EntityUid alarm) =>
        TryComp(alarm, out AirAlarmComponent? air) && air.SensorData.Values.Any(sensor => sensor.Pressure < 45f) ||
        _atmos.GetTileMixture(alarm) is { Pressure: < 45f };

    private bool IsCableMissing(EntityUid target, EngineeringServicePointComponent point)
    {
        var transform = Transform(target);
        return point.CableOffset is { } offset && transform.GridUid is { } grid &&
               TryComp(grid, out MapGridComponent? map) && !HasLocalCable(grid, map, transform.Coordinates.Offset(offset));
    }

    private bool HasLocalCable(EntityUid grid, MapGridComponent map, EntityCoordinates position)
    {
        var tile = _map.TileIndicesFor(grid, map, position);
        return _map.GetAnchoredEntities((grid, map), tile).Any(uid =>
            TryComp(uid, out CableComponent? cable) && cable.CableType == CableType.Apc);
    }

    private bool UpdateInfrastructure(EntityUid worker, EntityUid target, TransformComponent transform)
    {
        var order = Comp<EngineeringRepairComponent>(worker);
        if (order.CableInstalled)
        {
            order.Stage = "living-station-engineering-stage-power";
            if (_timing.CurTime >= order.VerifyPowerAfter)
            {
                var powered = TryComp(target, out EngineeringServicePointComponent? service) &&
                              service.PowerCheckOffset is { } check && transform.GridUid is { } grid &&
                              TryComp(grid, out MapGridComponent? map) &&
                              (HasPoweredLoad(grid, map, transform.Coordinates.Offset(check)) ||
                               service.CableOffset is { } cable && HasPoweredLoad(grid, map, transform.Coordinates.Offset(cable)));
                CompleteWork(worker, target, powered ? "living-station-engineering-work-power-restored" :
                    "living-station-engineering-work-cable", report: true);
            }
            return true;
        }

        if (!TryComp(target, out EngineeringServicePointComponent? point) || !NeedsInfrastructureWork(target))
            return false;

        var approach = transform.Coordinates.Offset(point.ApproachOffset);
        if (point.CargoPower && !order.Inspected)
        {
            foreach (var door in _lookup.GetEntitiesInRange<AirlockComponent>(Transform(worker).Coordinates, 1.3f, LookupFlags.Uncontained))
            {
                if (!TryComp<DoorComponent>(door.Owner, out var state) || state.State != DoorState.Closed ||
                    !_interaction.InRangeUnobstructed(worker, door.Owner) || !_doors.HasAccess(door.Owner, worker))
                    continue;

                if (_power.IsPowered(door.Owner))
                    _doors.TryOpen(door.Owner, state, worker, quiet: true);
            }
        }

        if (point.HullOffset != null && !PrepareEva(worker))
        {
            CloseReachableFirelocks(worker, target);
            CompleteWork(worker, target, "living-station-engineering-work-eva", report: true);
            return true;
        }

        var approachRange = point.CargoPower ? 0.35f : 1.25f;
        if (!order.Inspected && point.ApproachWaypoints.Count > order.NextApproachWaypoint &&
            !_interaction.InRangeUnobstructed(worker, approach, approachRange))
        {
            var waypoint = transform.Coordinates.Offset(point.ApproachWaypoints[order.NextApproachWaypoint]);
            if (_interaction.InRangeUnobstructed(worker, waypoint, 0.8f))
                order.NextApproachWaypoint++;
            else if (!ApproachMaintenance(worker, waypoint, 0.75f))
                CompleteWork(worker, target, "living-station-engineering-work-access");
            return true;
        }

        if (!order.Inspected && !_interaction.InRangeUnobstructed(worker, approach, approachRange))
        {
            if (!ApproachMaintenance(worker, approach, approachRange))
                CompleteWork(worker, target, "living-station-engineering-work-access");
            return true;
        }
        order.Inspected = true;

        if (point.CargoPower)
        {
            _power.SetPowerDisabled(target, false);
            CompleteWork(worker, target, "living-station-engineering-work-cargo-reset", report: true);
            return true;
        }

        if (point.CableOffset is { } offset && TryComp(target, out ApcComponent? apc))
        {
            RestoreLocalCable(worker, target, apc, transform.Coordinates.Offset(offset), IsCableMissing(target, point));
            return true;
        }

        if (point.HullOffset is { } hull)
            ContainBreach(worker, target, transform.Coordinates.Offset(hull));
        return true;
    }

    private bool HasPoweredLoad(EntityUid grid, MapGridComponent map, EntityCoordinates position) =>
        _lookup.GetEntitiesInTile(_map.GetTileRef(grid, map, position))
            .Any(uid => HasComp<ApcPowerReceiverComponent>(uid) && _power.IsPowered(uid));

    private void RestoreLocalCable(EntityUid worker, EntityUid target, ApcComponent apc, EntityCoordinates gap, bool missing)
    {
        if (!missing || !apc.MainBreakerEnabled || apc.LastExternalState == ApcExternalPowerState.None)
        {
            CompleteWork(worker, target, "living-station-engineering-work-upstream", report: true);
            return;
        }

        if (!TryGetStock(worker, target, LowVoltageCable, out var coil))
            return;

        if (!_interaction.InRangeUnobstructed(worker, gap))
        {
            if (!ApproachMaintenance(worker, gap, 0.35f))
                CompleteWork(worker, target, "living-station-engineering-work-access", report: true);
            return;
        }

        _steering.Unregister(worker);
        var installed = TryComp(coil, out CablePlacerComponent? placer) && placer.BlockingCableType == CableType.Apc &&
                        _cables.TryPlaceCable((coil, placer), worker, gap);
        if (!installed)
        {
            CompleteWork(worker, target, "living-station-engineering-work-cable-placement", report: true);
            return;
        }

        var order = Comp<EngineeringRepairComponent>(worker);
        order.CableInstalled = true;
        order.VerifyPowerAfter = _timing.CurTime + TimeSpan.FromSeconds(2);
    }

    private void ContainBreach(EntityUid worker, EntityUid target, EntityCoordinates hull)
    {
        CloseReachableFirelocks(worker, target);
        if (Transform(target).GridUid is not { } grid || !TryComp(grid, out MapGridComponent? map))
            return;

        var tile = _map.TileIndicesFor(grid, map, hull);
        if (!_map.GetTileRef(grid, map, tile).Tile.IsEmpty)
        {
            CompleteWork(worker, target, "living-station-engineering-work-breach", report: true);
            return;
        }

        if (!TryGetStock(worker, target, HullTile, out var plating))
            return;

        if (!_interaction.InRangeUnobstructed(worker, hull))
        {
            if (!ApproachMaintenance(worker, Transform(target).Coordinates.Offset(Comp<EngineeringServicePointComponent>(target).ApproachOffset), 0.35f))
                CompleteWork(worker, target, "living-station-engineering-work-access", report: true);
            return;
        }

        _steering.Unregister(worker);
        if (!_floors.CanPlaceTile(grid, map, tile, out _) || !_stacks.TryUse(plating, 1))
        {
            CompleteWork(worker, target, "living-station-engineering-work-access", report: true);
            return;
        }

        _map.SetTile(grid, map, tile, new Tile(ProtoMan.Index(Plating).TileId));
        CompleteWork(worker, target, "living-station-engineering-work-patched", report: true);
    }

    private void CloseReachableFirelocks(EntityUid worker, EntityUid target)
    {
        foreach (var firelock in _lookup.GetEntitiesInRange<FirelockComponent>(Transform(target).Coordinates, 5f, LookupFlags.Uncontained))
        {
            if (_interaction.InRangeAndAccessible(worker, firelock.Owner) &&
                _firelocks.CheckPressureAndFire(firelock, firelock.Comp).Pressure &&
                TryComp(firelock, out DoorComponent? door) && door.State == DoorState.Open)
                _doors.TryClose(firelock, door, worker);
        }
    }

    private bool PrepareEva(EntityUid worker)
    {
        // Pressure protection in this fork scales the measured pressure. Use the atmosphere floor for vacuum.
        if (!TryComp(worker, out BarotraumaComponent? barotrauma) || _pressure.GetFeltLowPressure(worker, barotrauma, 1f) < 45f ||
            !TryComp(worker, out InternalsComponent? internals))
            return false;

        if (!_internals.AreInternalsWorking(internals) && _internals.FindBestGasTank(worker) is { } tank &&
            _gasTanks.ConnectToInternals(tank, worker))
            Comp<EngineeringRepairComponent>(worker).ConnectedTank = tank.Owner;

        return _internals.AreInternalsWorking(internals) &&
               TryComp(internals.GasTankEntity, out GasTankComponent? gasTank) && !gasTank.IsLowPressure;
    }

    private bool TryGetStock(EntityUid worker, EntityUid target, ProtoId<StackPrototype> material, out EntityUid item)
    {
        item = default;
        var order = Comp<EngineeringRepairComponent>(worker);
        if (order.CarriedSupply is { } carried && Exists(carried) && _hands.EnumerateHeld(worker).Contains(carried))
        {
            item = carried;
            return true;
        }

        if (_inventory.TryGetSlotEntity(worker, "back", out var backpack) &&
            TryComp(backpack, out StorageComponent? pack))
        {
            var reserve = pack.Container.ContainedEntities.FirstOrDefault(uid =>
                TryComp(uid, out StackComponent? stack) && stack.StackTypeId == material && stack.Count > 0);
            if (reserve.IsValid() && TryCarryStock(worker, order, reserve))
            {
                item = reserve;
                return true;
            }
        }

        var rooms = EntityQueryEnumerator<CargoStockroomComponent, EntityStorageComponent, TransformComponent>();
        order.Stage = "living-station-engineering-stage-stock";
        while (rooms.MoveNext(out var room, out var stockroom, out var storage, out var position))
        {
            if (stockroom.Department != CargoDepartment.Engineering || position.GridUid != Transform(worker).GridUid)
                continue;

            if (storage.Open)
                break;

            var supplies = storage.Contents.ContainedEntities.FirstOrDefault(uid =>
                TryComp(uid, out StackComponent? stack) && stack.StackTypeId == material && stack.Count > 0 &&
                !EntityManager.IsQueuedForDeletion(uid));
            if (!supplies.IsValid())
            {
                _cargo.RequestEngineeringRestock(room);
                break;
            }

            var cabinetApproach = position.Coordinates.Offset(System.Numerics.Vector2.UnitX);
            if (!_interaction.InRangeAndAccessible(worker, room))
            {
                if (!ApproachMaintenance(worker, cabinetApproach, 0.35f))
                    CompleteWork(worker, target, "living-station-engineering-work-stock-access");
                return false;
            }

            _steering.Unregister(worker);
            if (_serviceStorage.Remove(supplies, room, storage))
            {
                if (TryCarryStock(worker, order, supplies))
                {
                    item = supplies;
                    return true;
                }
                _serviceStorage.Insert(supplies, room, storage);
            }

            break;
        }

        CompleteWork(worker, target, "living-station-engineering-work-supplies", report: true);
        return false;
    }

    private bool TryCarryStock(EntityUid worker, EngineeringRepairComponent order, EntityUid supply)
    {
        if (!_hands.TryGetEmptyHand(worker, out _) && FindHeldTool<WelderComponent>(worker) is { } welder &&
            _inventory.TryGetSlotEntity(worker, "back", out var pack) &&
            _packStorage.Insert(pack.Value, welder, out _, worker))
            order.StoredTool = welder;

        if (!_hands.TryPickupAnyHand(worker, supply))
            return false;

        order.CarriedSupply = supply;
        return true;
    }

    private void PutAwayMaintenanceSupplies(EntityUid worker, EngineeringRepairComponent order)
    {
        if (order.ConnectedTank is { } tank && TryComp(tank, out GasTankComponent? gasTank) &&
            _atmos.GetTileMixture(worker) is { Pressure: >= 75f })
            _gasTanks.DisconnectFromInternals((tank, gasTank), worker);

        if (TerminatingOrDeleted(worker) || !_inventory.TryGetSlotEntity(worker, "back", out var pack))
            return;

        if (order.CarriedSupply is { } supply && _hands.EnumerateHeld(worker).Contains(supply) &&
            !EntityManager.IsQueuedForDeletion(supply) &&
            !_packStorage.Insert(pack.Value, supply, out _, worker))
            _hands.TryDrop(worker, supply);

        if (order.StoredTool is { } tool && Exists(tool) && Transform(tool).ParentUid == pack)
            _hands.TryPickupAnyHand(worker, tool);
    }

    private bool EnsureWelderFuel(EntityUid worker, EntityUid welder, float required, EntityUid target)
    {
        var tool = Comp<WelderComponent>(welder);
        var ignitionFuel = tool.FuelLitCost;
        var minimum = required + (_toggle.IsActivated(welder) ? 0 : ignitionFuel.Float());
        if (_tools.GetWelderFuelAndCapacity(welder, tool).fuel.Float() >= minimum)
            return true;

        Comp<EngineeringRepairComponent>(worker).Stage = "living-station-engineering-stage-fuel";
        _toggle.TryDeactivate(welder, worker, predicted: false);
        var tank = _lookup.GetEntitiesInRange<ReagentTankComponent>(Transform(worker).Coordinates, 20f, LookupFlags.Uncontained)
            .Where(tank => tank.Comp.TankType == ReagentTankType.Fuel && Transform(tank.Owner).GridUid == Transform(worker).GridUid &&
                           _solutions.TryGetDrainableSolution(tank.Owner, out _, out var fuel) && fuel.Volume > 0)
            .OrderBy(tank => (Transform(tank.Owner).LocalPosition - Transform(worker).LocalPosition).LengthSquared())
            .FirstOrDefault();
        if (tank.Owner.IsValid())
        {
            if (!_interaction.InRangeAndAccessible(worker, tank.Owner))
            {
                if (ApproachMaintenance(worker, Transform(tank.Owner).Coordinates, 1.25f))
                    return false;
            }
            else
            {
                _steering.Unregister(worker);
                _interaction.InteractUsing(worker, welder, tank, Transform(tank.Owner).Coordinates);
                if (_tools.GetWelderFuelAndCapacity(welder, tool).fuel.Float() >= required + ignitionFuel.Float())
                    return false;
            }
        }

        CompleteWork(worker, target, "living-station-engineering-work-fuel", report: true);
        return false;
    }

    private void CompleteWork(EntityUid worker, EntityUid target, LocId status, bool report = false)
    {
        var message = MaintenanceResult(target, status);
        _lastWorkResult = message;
        if (TryComp<EngineeringRepairComponent>(worker, out var order) && order.Requester is { } requester)
        {
            UpdateRepairMemory(requester, target, status);
            NotifyRequester(requester, message);
            QueuePersonalUpdate(worker, requester, message);
        }
        if (report && _blocker.CanSpeak(worker) && TryComp(worker, out WearingHeadsetComponent? headset))
            _radio.SendRadioMessage(worker, message, ProtoMan.Index(EngineeringChannel), headset.Headset);
        Finish(worker, message, success: status.Id is "living-station-engineering-work-complete" or
            "living-station-engineering-work-power-restored" or "living-station-engineering-work-cargo-reset" or
            "living-station-engineering-work-patched");
    }

    private string MaintenanceResult(EntityUid target, LocId status) =>
        Loc.GetString(status, ("target", Exists(target) ? Name(target) : Loc.GetString("living-station-engineering-work-idle")));

    private void NotifyRequester(EntityUid requester, string message)
    {
        if (!TerminatingOrDeleted(requester))
            _popup.PopupEntity(message, requester, requester);
    }
}
