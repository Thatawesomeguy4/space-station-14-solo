using System.Linq;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.NPC;
using Content.Shared.Placeable;
using Content.Shared.Popups;
using Content.Shared.Station.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Verbs;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationCargoSystem : EntitySystem
{
    private static readonly EntProtoId WorkerPrototype = "MobLivingStationCargoTechnician";
    private static readonly EntProtoId CratePrototype = "CrateGenericSteel";
    private static readonly EntProtoId DropoffPrototype = "LivingStationCargoDropoff";
    private static readonly EntProtoId MedicalKit = "LivingStationMedicalChemistryKit";
    private static readonly EntProtoId MedicalSupplies = "CrateMedicalSupplies";
    private static readonly EntProtoId ElectricalSupplies = "CrateEngineeringElectricalSupplies";
    private static readonly EntProtoId JanitorialSupplies = "CrateJanitorialSupplies";
    private static readonly (EntProtoId Product, string Verb)[] RequisitionCatalog =
    [
        (MedicalSupplies, "living-station-cargo-order-medical"),
        (ElectricalSupplies, "living-station-cargo-order-electrical"),
        ("CrateMaterialSteel", "living-station-cargo-order-steel"),
        ("CrateMaterialGlass", "living-station-cargo-order-glass"),
        (JanitorialSupplies, "living-station-cargo-order-janitorial"),
    ];
    private const int QueueLimit = 8;
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DepartmentDeliveryTimeout = TimeSpan.FromSeconds(90);
    private const float PullRange = 1.05f;
    private TimeSpan _nextDispatch;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<CargoDeliveryPickupComponent, GetVerbsEvent<Verb>>(OnGetDeliveryVerbs);
        SubscribeLocalEvent<CargoDeliveryComponent, ComponentShutdown>(OnDeliveryShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void StartDelivery(MapId mapId)
    {
        var markers = EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        EntityUid? pickup = null;
        EntityUid? dropoff = null;
        while (markers.MoveNext(out var uid, out var metadata, out var xform))
        {
            if (xform.MapID != mapId)
                continue;

            if (HasComp<CargoDeliveryPickupComponent>(uid))
                pickup = uid;
            else if (metadata.EntityPrototype?.ID == DropoffPrototype.Id)
                dropoff = uid;
        }

        if (pickup is not { } pickupUid || dropoff is not { } dropoffUid)
            return;

        var pickupTransform = Transform(pickupUid);
        var dropoffTransform = Transform(dropoffUid);
        if (pickupTransform.GridUid is not { } grid || dropoffTransform.GridUid != grid ||
            _station.GetOwningStation(pickupUid, pickupTransform) == null)
            return;

        var workerOffset = pickupTransform.LocalRotation.RotateVec(Vector2.UnitX);
        var worker = SpawnAttachedTo(WorkerPrototype, pickupTransform.Coordinates.Offset(workerOffset));
        var crate = SpawnAttachedTo(CratePrototype, pickupTransform.Coordinates);
        if (!TryAssignDelivery(worker, crate, dropoffUid))
        {
            QueueDel(crate);
            QueueDel(worker);
            return;
        }

        var stationPickup = Comp<CargoDeliveryPickupComponent>(pickupUid);
        stationPickup.Worker = worker;
        stationPickup.Dropoff = dropoffUid;
    }

    private void OnGetDeliveryVerbs(Entity<CargoDeliveryPickupComponent> pickup, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !Enabled || pickup.Comp.Worker == null)
            return;

        var requester = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString(pickup.Comp.AutomaticHauling
                ? "living-station-cargo-disable-auto"
                : "living-station-cargo-enable-auto"),
            Act = () =>
            {
                if (CanUsePickup(pickup, requester))
                    pickup.Comp.AutomaticHauling = !pickup.Comp.AutomaticHauling;
            },
        });

        if (FindDeliveryCrate(pickup) != null)
        {
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("living-station-cargo-request-delivery"),
                Act = () =>
                {
                    if (CanUsePickup(pickup, requester) &&
                        FindDeliveryCrate(pickup) is { } crate && pickup.Comp.Worker is { } worker)
                        TryAssignDelivery(worker, crate, pickup.Comp.Dropoff);
                },
            });
        }

        foreach (var (product, label) in RequisitionCatalog)
        {
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString(label),
                Act = () =>
                {
                    if (!CanUsePickup(pickup, requester))
                        return;

                    var accepted = TryQueueRequisition(pickup, requester, product);
                    _popup.PopupEntity(Loc.GetString(accepted
                        ? "living-station-cargo-order-queued"
                        : "living-station-cargo-order-unavailable"), pickup, requester);
                },
            });
        }

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-check-queue"),
            Act = () =>
            {
                if (!CanUsePickup(pickup, requester))
                    return;

                var pending = pickup.Comp.Requisitions;
                var status = pending.Count == 0
                    ? Loc.GetString("living-station-cargo-queue-empty")
                    : Loc.GetString("living-station-cargo-queue-status", ("count", pending.Count),
                        ("next", ProtoMan.Index(pending[0].Product).Name));
                if (TryComp(pickup.Comp.Worker, out CargoDeliveryComponent? active) && EntityManager.EntityExists(active.Crate))
                    status = Loc.GetString("living-station-cargo-queue-active", ("crate", Name(active.Crate)), ("status", status));
                _popup.PopupEntity(status, pickup, requester);
            },
        });

        if (!pickup.Comp.Requisitions.Any(order => order.Requester == requester))
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-cancel-orders"),
            Act = () =>
            {
                if (!CanUsePickup(pickup, requester))
                    return;

                var count = pickup.Comp.Requisitions.RemoveAll(order => order.Requester == requester);
                _popup.PopupEntity(Loc.GetString("living-station-cargo-orders-cancelled", ("count", count)), pickup, requester);
            },
        });
    }

    private bool Enabled => _ticker.RunLevel == GameRunLevel.InRound && _config.GetCVar(CCVars.LivingStationEnabled);

    private bool CanUsePickup(Entity<CargoDeliveryPickupComponent> pickup, EntityUid requester) =>
        Enabled && !TerminatingOrDeleted(pickup) && _mobState.IsAlive(requester) &&
        _blocker.CanInteract(requester, pickup) && _interaction.InRangeAndAccessible(requester, pickup.Owner);

    private EntityUid? FindDeliveryCrate(Entity<CargoDeliveryPickupComponent> pickup)
    {
        if (!Enabled || !TryGetDispatchRoute(pickup, out var pickupTransform, out var mapGrid, out _) ||
            !IsDropoffClear(pickup.Comp.Dropoff, pickupTransform.GridUid!.Value, mapGrid))
            return null;

        foreach (var crate in CratesOnPallet(pickupTransform, pickupTransform.GridUid!.Value, mapGrid).OrderBy(crate => crate))
        {
            if (CanHaul(crate))
                return crate;
        }

        return null;
    }

    private bool CanHaul(EntityUid crate) =>
        !TerminatingOrDeleted(crate) && !EntityManager.IsQueuedForDeletion(crate) &&
        HasComp<PlaceableSurfaceComponent>(crate) &&
        TryComp(crate, out PullableComponent? pullable) && pullable.Puller == null &&
        TryComp(crate, out EntityStorageComponent? storage) && !storage.Open &&
        !Transform(crate).Anchored;

    internal bool RequestSupplies(EntityUid requester, EntProtoId product)
    {
        var pickups = EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
        while (pickups.MoveNext(out var pickup, out var route, out var transform))
        {
            if (Transform(requester).GridUid is { } grid && transform.GridUid == grid)
                return TryQueueRequisition((pickup, route), requester, product);
        }

        return false;
    }

    private bool TryQueueRequisition(Entity<CargoDeliveryPickupComponent> pickup, EntityUid requester, EntProtoId product)
    {
        var queue = pickup.Comp.Requisitions;
        var request = new CargoRequisition(requester, product);
        if (!Enabled || pickup.Comp.Worker is not { } worker || !_mobState.IsAlive(worker) ||
            queue.Count >= QueueLimit || queue.Contains(request) ||
            TryComp(worker, out CargoDeliveryComponent? delivery) && delivery.Requisition == request)
            return false;

        queue.Add(request);
        return true;
    }

    private bool TryGetDispatchRoute(Entity<CargoDeliveryPickupComponent> pickup,
        out TransformComponent pickupTransform, out MapGridComponent mapGrid, out EntityUid worker)
    {
        pickupTransform = default!;
        mapGrid = default!;
        worker = pickup.Comp.Worker.GetValueOrDefault();
        if (!_mobState.IsAlive(worker) || HasComp<ActorComponent>(worker) ||
            HasComp<LivingStationWorkClaimComponent>(worker) || !TryComp(pickup, out TransformComponent? source) ||
            source.GridUid is not { } grid || !TryComp(grid, out MapGridComponent? gridComponent) ||
            Transform(worker).GridUid != grid)
            return false;

        pickupTransform = source;
        mapGrid = gridComponent;
        return true;
    }

    private bool IsDropoffClear(EntityUid pallet, EntityUid grid, MapGridComponent mapGrid) =>
        TryComp(pallet, out TransformComponent? transform) && transform.GridUid == grid &&
        !CratesOnPallet(transform, grid, mapGrid).Any();

    private EntityUid? FindDepartmentPallet(EntityUid grid, CargoDepartment department)
    {
        var pallets = EntityQueryEnumerator<CargoDepartmentPalletComponent, TransformComponent>();
        while (pallets.MoveNext(out var uid, out var pallet, out var transform))
        {
            if (transform.GridUid == grid && pallet.Department == department)
                return uid;
        }
        return null;
    }

    private EntityUid GetDestination(EntityUid grid, EntProtoId product, EntityUid fallback)
    {
        if (product == MedicalKit || product == MedicalSupplies)
            return FindDepartmentPallet(grid, CargoDepartment.Medical) ?? fallback;
        if (product == ElectricalSupplies)
            return FindDepartmentPallet(grid, CargoDepartment.Engineering) ?? fallback;
        if (product == JanitorialSupplies)
            return FindDepartmentPallet(grid, CargoDepartment.Janitorial) ?? fallback;
        return fallback;
    }

    private void DispatchRequisitions()
    {
        var pickups = EntityQueryEnumerator<CargoDeliveryPickupComponent>();
        while (pickups.MoveNext(out var pickup, out var route))
        {
            route.Requisitions.RemoveAll(order => TerminatingOrDeleted(order.Requester));
            if (!TryGetDispatchRoute((pickup, route), out var transform, out var mapGrid, out var worker))
                continue;

            var grid = transform.GridUid!.Value;
            var staged = CratesOnPallet(transform, grid, mapGrid).OrderBy(crate => crate).ToArray();
            if (staged.Length != 0)
            {
                var freight = staged.FirstOrDefault(crate => CanHaul(crate) && !HasComp<MedicalChemistryKitComponent>(crate));
                if (route.AutomaticHauling && freight.IsValid() && IsDropoffClear(route.Dropoff, grid, mapGrid))
                {
                    TryAssignDelivery(worker, freight, route.Dropoff);
                    continue;
                }
            }

            if (staged.Length == 0 && TryDispatchRequisition(route, transform, mapGrid, worker, grid))
                continue;

            if (_timing.CurTime >= route.NextReturnAttempt &&
                FindDepartmentPallet(grid, CargoDepartment.Returns) is { } returns &&
                IsDropoffClear(returns, grid, mapGrid))
                TryDispatchReturn(route, worker, grid, mapGrid, returns);
        }
    }

    private bool TryDispatchRequisition(CargoDeliveryPickupComponent route, TransformComponent source,
        MapGridComponent mapGrid, EntityUid worker, EntityUid grid)
    {
        for (var i = 0; i < route.Requisitions.Count; i++)
        {
            var order = route.Requisitions[i];
            var destination = GetDestination(grid, order.Product, route.Dropoff);
            if (!IsDropoffClear(destination, grid, mapGrid))
                continue;

            var crate = SpawnAttachedTo(order.Product, source.Coordinates);
            if (!TryAssignDelivery(worker, crate, destination, order))
            {
                QueueDel(crate);
                return false;
            }

            route.Requisitions.RemoveAt(i);
            if (HasComp<MedicalChemistryKitComponent>(crate) &&
                TryComp(order.Requester, out LivingStationMedicComponent? medic))
                medic.ChemistryKit = crate;
            return true;
        }

        return false;
    }

    private void TryDispatchReturn(CargoDeliveryPickupComponent route, EntityUid worker, EntityUid grid,
        MapGridComponent mapGrid, EntityUid returns)
    {
        var pallets = EntityQueryEnumerator<CargoDepartmentPalletComponent, TransformComponent>();
        while (pallets.MoveNext(out _, out var pallet, out var transform))
        {
            if (transform.GridUid != grid || pallet.Department == CargoDepartment.Returns)
                continue;

            foreach (var crate in CratesOnPallet(transform, grid, mapGrid).OrderBy(crate => crate))
            {
                if (CanHaul(crate) && Comp<EntityStorageComponent>(crate).Contents.ContainedEntities.Count == 0 &&
                    TryAssignDelivery(worker, crate, returns, emptyReturn: true))
                {
                    route.NextReturnAttempt = _timing.CurTime + TimeSpan.FromSeconds(15);
                    return;
                }
            }
        }
    }

    private IEnumerable<EntityUid> CratesOnPallet(TransformComponent pallet, EntityUid grid, MapGridComponent mapGrid)
    {
        var tile = _map.TileIndicesFor(grid, mapGrid, pallet.Coordinates);
        foreach (var crate in _lookup.GetEntitiesInRange<EntityStorageComponent>(pallet.Coordinates, 0.71f, LookupFlags.Uncontained))
        {
            var transform = Transform(crate);
            if (transform.ParentUid == grid && _map.TileIndicesFor(grid, mapGrid, transform.Coordinates) == tile)
                yield return crate;
        }
    }

    private bool TryAssignDelivery(EntityUid worker, EntityUid crate, EntityUid dropoff,
        CargoRequisition? requisition = null, bool emptyReturn = false)
    {
        var timeout = HasComp<CargoDepartmentPalletComponent>(dropoff) ? DepartmentDeliveryTimeout : DeliveryTimeout;
        if (!_work.TryAssign(worker, new EntityCoordinates(crate, Vector2.Zero), timeout))
            return false;

        var order = AddComp<CargoDeliveryComponent>(worker);
        order.Crate = crate;
        order.Dropoff = dropoff;
        order.Requisition = requisition;
        order.EmptyReturn = emptyReturn;
        EnsureComp<ActiveNPCComponent>(worker);
        return true;
    }

    public override void Update(float frameTime)
    {
        if (Enabled && _timing.CurTime >= _nextDispatch)
        {
            _nextDispatch = _timing.CurTime + TimeSpan.FromSeconds(1);
            DispatchRequisitions();
        }

        var orders = EntityQueryEnumerator<CargoDeliveryComponent, TransformComponent>();
        while (orders.MoveNext(out var worker, out var order, out var workerTransform))
        {
            if (!TryComp(worker, out LivingStationWorkClaimComponent? claim) ||
                !TryComp(order.Crate, out PullableComponent? crate) ||
                !TryComp(order.Crate, out TransformComponent? crateTransform) || crateTransform.Anchored ||
                !TryComp(order.Crate, out EntityStorageComponent? storage) || storage.Open ||
                order.EmptyReturn && storage.Contents.ContainedEntities.Count != 0 ||
                !TryComp(order.Dropoff, out TransformComponent? dropoff) ||
                dropoff.GridUid is not { } grid || crateTransform.ParentUid != grid || workerTransform.GridUid != grid ||
                !TryComp(grid, out MapGridComponent? mapGrid) || !_mobState.IsAlive(worker) || HasComp<ActorComponent>(worker))
            {
                Finish(worker, "cancelled because the worker, crate, or drop-off is unavailable");
                continue;
            }

            var deliveryTile = _map.TileIndicesFor(grid, mapGrid, dropoff.Coordinates);
            if (CratesOnPallet(dropoff, grid, mapGrid).Any(other => other != order.Crate))
            {
                Finish(worker, "cancelled because the drop-off became occupied");
                continue;
            }

            if (_map.TileIndicesFor(grid, mapGrid, crateTransform.Coordinates) == deliveryTile)
            {
                order.Completed = true;
                if (TryComp(order.Crate, out MedicalChemistryKitComponent? kit))
                    kit.Delivered = true;

                Finish(worker, "completed");
                continue;
            }

            if (crate.Puller != worker && (order.Pulling || crate.Puller != null))
            {
                Finish(worker, "cancelled because the crate pull was interrupted");
                continue;
            }

            if (_timing.CurTime >= claim.Deadline)
            {
                Finish(worker, $"timed out {(order.Pulling ? "pulling" : "approaching")} the crate from {workerTransform.Coordinates}");
                continue;
            }

            if (!TryComp(worker, out NPCSteeringComponent? steering))
            {
                // The round can start before pathfinding has built the grid's first navigation chunks.
                if (order.Pulling)
                    Finish(worker, "lost steering while pulling the crate");
                else if (_pathfinding.GetPoly(workerTransform.Coordinates) != null && _pathfinding.GetPoly(dropoff.Coordinates) != null)
                    _steering.Register(worker, claim.Target).Range = PullRange;

                continue;
            }

            if (steering.Status == SteeringStatus.NoPath)
            {
                Finish(worker, $"could not reach the {(order.Pulling ? "drop-off" : "crate")} from {workerTransform.Coordinates}");
                continue;
            }

            if (order.Pulling || !_interaction.InRangeUnobstructed(worker, order.Crate, PullRange))
                continue;

            if (!_pulling.TryStartPull(worker, order.Crate, pullableComp: crate))
            {
                Finish(worker, "failed to pull the crate");
                continue;
            }

            order.Pulling = true;
            // The worker walks past the pallet so the crate trailing on the pull joint reaches it.
            var direction = dropoff.LocalRotation.RotateVec(Vector2.UnitX);
            _steering.Register(worker, dropoff.Coordinates.Offset(direction * 1.3f), steering).Range = 0.15f;
        }
    }

    private void Finish(EntityUid worker, string reason)
    {
        Log.Info($"Living Station cargo delivery {reason} for {ToPrettyString(worker)}.");
        RemComp<CargoDeliveryComponent>(worker);
    }

    private void OnDeliveryShutdown(EntityUid uid, CargoDeliveryComponent order, ComponentShutdown args)
    {
        if (TryComp(order.Crate, out PullableComponent? crate) && crate.Puller == uid)
            _pulling.TryStopPull(order.Crate, crate, uid);

        if (!order.Completed && order.Requisition is { } request && HasComp<MedicalChemistryKitComponent>(order.Crate) &&
            TryComp(request.Requester, out LivingStationMedicComponent? medic) && medic.ChemistryKit == order.Crate)
            medic.ChemistryKit = null;

        _steering.Unregister(uid);
        RemComp<ActiveNPCComponent>(uid);
        _work.Release(uid);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        _nextDispatch = TimeSpan.Zero;
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var orders = AllEntityQuery<CargoDeliveryComponent>();
        while (orders.MoveNext(out var worker, out _))
            RemComp<CargoDeliveryComponent>(worker);

        var pickups = AllEntityQuery<CargoDeliveryPickupComponent>();
        while (pickups.MoveNext(out var pickup))
        {
            pickup.Requisitions.Clear();
            pickup.AutomaticHauling = false;
        }
    }
}
