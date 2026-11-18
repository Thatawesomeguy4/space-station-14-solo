using System.Linq;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
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
using Robust.Shared.Physics.Systems;
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
    private static readonly EntProtoId MinedSteel = "LivingStationMinedSteel";
    private static readonly EntProtoId MinedGlass = "LivingStationMinedGlass";
    private static readonly (EntProtoId Product, string Verb, int Cost)[] RequisitionCatalog =
    [
        (MedicalSupplies, "living-station-cargo-order-medical", 900),
        (ElectricalSupplies, "living-station-cargo-order-electrical", 1200),
        ("CrateMaterialSteel", "living-station-cargo-order-steel", 1500),
        ("CrateMaterialGlass", "living-station-cargo-order-glass", 1500),
        (JanitorialSupplies, "living-station-cargo-order-janitorial", 800),
        (MinedSteel, "living-station-cargo-order-mined-steel", 0),
        (MinedGlass, "living-station-cargo-order-mined-glass", 0),
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
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
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
        InitializeCargoLogistics();
        SubscribeLocalEvent<CargoDeliveryComponent, ComponentShutdown>(OnDeliveryShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal int StartDelivery(MapId mapId, int count)
    {
        if (count == 0)
            return 0;
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
            return 0;

        var pickupTransform = Transform(pickupUid);
        var dropoffTransform = Transform(dropoffUid);
        if (pickupTransform.GridUid is not { } grid || dropoffTransform.GridUid != grid ||
            _station.GetOwningStation(pickupUid, pickupTransform) == null)
            return 0;

        var workerOffset = pickupTransform.LocalRotation.RotateVec(Vector2.UnitX);
        var worker = SpawnAttachedTo("MobLivingStationQuartermaster", pickupTransform.Coordinates.Offset(workerOffset));
        var crate = SpawnAttachedTo(CratePrototype, pickupTransform.Coordinates);
        if (!TryAssignDelivery(worker, crate, dropoffUid))
        {
            QueueDel(crate);
            QueueDel(worker);
            return 0;
        }

        var stationPickup = Comp<CargoDeliveryPickupComponent>(pickupUid);
        stationPickup.Dropoff = dropoffUid;
        stationPickup.Workers.Add(worker);
        var staffing = EntityManager.System<LivingStationSystem>();
        staffing.RegisterCrew(worker, LivingStationDepartment.Cargo);
        EntityManager.System<LivingStationIncidentSystem>().RegisterDepartmentHead("Quartermaster", worker);
        return 1 + staffing.SpawnSupportCrew(LivingStationDepartment.Cargo, WorkerPrototype,
            pickupTransform.Coordinates.Offset(workerOffset), count - 1, stationPickup.Workers);
    }

    private void OnGetDeliveryVerbs(Entity<CargoDeliveryPickupComponent> pickup, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !Enabled || pickup.Comp.Workers.Count == 0)
            return;

        var requester = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString(pickup.Comp.AutomaticHauling
                ? "living-station-cargo-disable-auto"
                : "living-station-cargo-enable-auto"),
            Act = () =>
            {
                if (CanUseCargoTerminal(pickup, requester))
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
                    if (CanUseCargoTerminal(pickup, requester) &&
                        FindDeliveryCrate(pickup) is { } crate && TryGetDispatchRoute(pickup, out _, out _, out var worker))
                        TryAssignDelivery(worker, crate, pickup.Comp.Dropoff);
                },
            });
        }

        AddRequisitionVerbs(pickup, pickup, requester, args.Verbs);
    }

    private void AddRequisitionVerbs(Entity<CargoDeliveryPickupComponent> pickup, EntityUid terminal, EntityUid requester,
        SortedSet<Verb> verbs)
    {
        foreach (var (product, label, cost) in RequisitionCatalog)
        {
            if (GetRequestedMaterial(product) != null && (Transform(pickup).GridUid is not { } grid ||
                FindDepartmentPallet(grid, CargoDepartment.Materials) == null))
                continue;

            verbs.Add(new Verb
            {
                Text = Loc.GetString(label),
                Message = Loc.GetString("living-station-cargo-order-cost", ("cost", cost)),
                Act = () =>
                {
                    if (TerminatingOrDeleted(pickup) || !CanUseCargoTerminal(terminal, requester))
                        return;

                    var accepted = TryQueueRequisition(pickup, requester, product);
                    _popup.PopupEntity(Loc.GetString(accepted
                        ? "living-station-cargo-order-queued"
                        : "living-station-cargo-order-unavailable"), terminal, requester);
                },
            });
        }

        verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-check-queue"),
            Act = () =>
            {
                if (TerminatingOrDeleted(pickup) || !CanUseCargoTerminal(terminal, requester))
                    return;

                var pending = pickup.Comp.Requisitions;
                var status = pending.Count == 0
                    ? Loc.GetString("living-station-cargo-queue-empty")
                    : Loc.GetString("living-station-cargo-queue-status", ("orders", string.Join(", ", pending.Select(DescribeRequisition))));
                foreach (var worker in pickup.Comp.Workers)
                {
                    if (TryComp(worker, out CargoDeliveryComponent? active) && Exists(active.Crate) && Exists(active.Dropoff))
                        status = Loc.GetString("living-station-cargo-queue-active", ("crate", Name(active.Crate)),
                            ("destination", Name(active.Dropoff)), ("status", status));
                }
                var balance = GetCargoBalance(_station.GetOwningStation(terminal));
                status = Loc.GetString("living-station-cargo-queue-balance", ("balance", balance), ("status", status));
                if (pickup.Comp.OreIssue is { } issue)
                    status += " " + Loc.GetString(issue);
                _popup.PopupEntity(status, terminal, requester);
            },
        });

        if (!pickup.Comp.Requisitions.Any(order => order.Requester == requester))
            return;

        verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-cargo-cancel-orders"),
            Act = () =>
            {
                if (TerminatingOrDeleted(pickup) || !CanUseCargoTerminal(terminal, requester))
                    return;

                var count = pickup.Comp.Requisitions.RemoveAll(order => order.Requester == requester);
                _popup.PopupEntity(Loc.GetString("living-station-cargo-orders-cancelled", ("count", count)), terminal, requester);
            },
        });
    }

    private bool Enabled => _ticker.RunLevel == GameRunLevel.InRound && _config.GetCVar(CCVars.LivingStationEnabled);

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
        // The Medbay staff share one receiving pallet and medication stock.
        if (product == MedicalKit && (queue.Any(order => order.Product == MedicalKit) ||
            pickup.Comp.Workers.Any(worker => TryComp<CargoDeliveryComponent>(worker, out var delivery) &&
                                              delivery.Requisition?.Product == MedicalKit)))
            return false;

        if (!Enabled || !pickup.Comp.Workers.Any(worker => _mobState.IsAlive(worker)) ||
            queue.Count >= QueueLimit || queue.Contains(request) ||
            pickup.Comp.Workers.Any(worker => TryComp(worker, out CargoDeliveryComponent? delivery) && delivery.Requisition == request))
            return false;

        queue.Add(request);
        return true;
    }

    private bool TryGetDispatchRoute(Entity<CargoDeliveryPickupComponent> pickup,
        out TransformComponent pickupTransform, out MapGridComponent mapGrid, out EntityUid worker)
    {
        pickupTransform = default!;
        mapGrid = default!;
        worker = default;
        if (!TryComp(pickup, out TransformComponent? source) ||
            source.GridUid is not { } grid || !TryComp(grid, out MapGridComponent? gridComponent))
            return false;

        worker = pickup.Comp.Workers.FirstOrDefault(uid => _mobState.IsAlive(uid) && !HasComp<ActorComponent>(uid) &&
            !HasComp<LivingStationWorkClaimComponent>(uid) && Transform(uid).GridUid == grid);
        pickupTransform = source;
        mapGrid = gridComponent;
        return worker.IsValid();
    }

    private bool IsDropoffClear(EntityUid pallet, EntityUid grid, MapGridComponent mapGrid) =>
        !DropoffReserved(pallet) && TryComp(pallet, out TransformComponent? transform) && transform.GridUid == grid &&
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
        if (product == ElectricalSupplies || product == EngineeringStock)
            return FindDepartmentPallet(grid, CargoDepartment.Engineering) ?? fallback;
        if (product == JanitorialSupplies || product == JanitorialStock)
            return FindDepartmentPallet(grid, CargoDepartment.Janitorial) ?? fallback;
        if (product == MinedSteel)
            return FindDepartmentPallet(grid, CargoDepartment.Engineering) ?? fallback;
        if (product == MinedGlass)
            return FindDepartmentPallet(grid, CargoDepartment.Science) ?? fallback;
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
            ProcessStagedOre(route, grid, mapGrid, worker);
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

            if (TryDispatchOre(route, grid, mapGrid, worker))
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
            List<EntProtoId>? stock = null;
            if (TryComp(order.Requester, out CargoStockroomComponent? stockroom))
                stock = GetRestockItems(order.Requester, stockroom);
            if (stock is { Count: 0 })
            {
                route.Requisitions.RemoveAt(i--);
                continue;
            }
            var destination = GetDestination(grid, order.Product, route.Dropoff);
            if (!IsDropoffClear(destination, grid, mapGrid))
                continue;

            var cost = GetRequisitionCost(order.Product);
            var station = _station.GetOwningStation(grid);
            if (cost > 0 && GetCargoBalance(station) < cost)
                continue;

            var material = GetRequestedMaterial(order.Product);
            var supplies = material is { } stackType ? FindRefinedStacks(grid, stackType) : [];
            if (material != null && supplies.Sum(stack => stack.Comp.Count) < MaterialDeliverySize)
                continue;

            var crate = SpawnAttachedTo(order.Product, source.Coordinates);
            if (!TryAssignDelivery(worker, crate, destination, order))
            {
                QueueDel(crate);
                return false;
            }

            route.Requisitions.RemoveAt(i);
            if (material != null)
                PackRefinedMaterials(supplies, crate, source.Coordinates);
            if (stock != null)
            {
                foreach (var product in stock)
                    SpawnInContainerOrDrop(product, crate, "entity_storage");
            }
            if (cost > 0 && station is { } accountStation)
                _cargo.TryAdjustBankAccount(accountStation, CargoAccount, -cost);
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
            if (transform.GridUid != grid || pallet.Department is CargoDepartment.Returns or CargoDepartment.Export or
                CargoDepartment.OreIntake or CargoDepartment.Materials)
                continue;

            foreach (var crate in CratesOnPallet(transform, grid, mapGrid).OrderBy(crate => crate))
            {
                var checkedIn = TryComp<MedicalChemistryKitComponent>(crate, out var kit) && kit.CheckedIn;
                if (CanHaul(crate) && (checkedIn || Comp<EntityStorageComponent>(crate).Contents.ContainedEntities.Count == 0) &&
                    TryAssignDelivery(worker, crate, returns, emptyReturn: !checkedIn))
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
        if (DropoffReserved(dropoff))
            return false;

        var timeout = HasComp<CargoDepartmentPalletComponent>(dropoff) ? DepartmentDeliveryTimeout : DeliveryTimeout;
        if (!_work.TryAssign(worker, new EntityCoordinates(crate, Vector2.Zero), timeout, requisition?.Requester))
            return false;

        var order = AddComp<CargoDeliveryComponent>(worker);
        order.Crate = crate;
        order.Dropoff = dropoff;
        order.Requisition = requisition;
        order.EmptyReturn = emptyReturn;
        EnsureComp<ActiveNPCComponent>(worker);
        return true;
    }

    private bool DropoffReserved(EntityUid dropoff)
    {
        var orders = EntityQueryEnumerator<CargoDeliveryComponent>();
        while (orders.MoveNext(out var order))
        {
            if (order.Dropoff == dropoff)
                return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        if (Enabled && _timing.CurTime >= _nextDispatch)
        {
            _nextDispatch = _timing.CurTime + TimeSpan.FromSeconds(1);
            QueueStockroomOrders();
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
                Finish(worker, $"cancelled because the worker, crate, or drop-off is unavailable (crate {order.Crate}, worker at {workerTransform.Coordinates})");
                continue;
            }

            var deliveryTile = _map.TileIndicesFor(grid, mapGrid, dropoff.Coordinates);
            if (CratesOnPallet(dropoff, grid, mapGrid).Any(other => other != order.Crate))
            {
                Finish(worker, "cancelled because the drop-off became occupied");
                continue;
            }

            var atDropoff = _map.TileIndicesFor(grid, mapGrid, crateTransform.Coordinates) == deliveryTile;
            // Align freight with the pallet once it has been hauled alongside it.
            var canAlign = HasComp<CargoDepartmentPalletComponent>(order.Dropoff) && crate.Puller == worker &&
                           crateTransform.Coordinates.TryDistance(EntityManager, dropoff.Coordinates, out var gap) &&
                           gap <= 1.5f && _interaction.InRangeUnobstructed(order.Crate, dropoff.Coordinates, 1.5f);
            if (atDropoff || canAlign)
            {
                order.Completed = true;
                if (crate.Puller == worker)
                {
                    if (canAlign && _pulling.TryStopPull(order.Crate, crate, worker))
                        _transform.SetCoordinates(order.Crate, dropoff.Coordinates);
                    else if (!atDropoff)
                    {
                        order.Completed = false;
                        continue;
                    }

                    _physics.SetLinearVelocity(order.Crate, Vector2.Zero);
                }
                if (TryComp(order.Crate, out MedicalChemistryKitComponent? kit) && !kit.Delivered)
                {
                    kit.Delivered = true;
                    _chat.TrySendInGameICMessage(worker, Loc.GetString("living-station-cargo-chemicals-delivered"),
                        InGameICChatType.Speak, ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false);
                }
                if (order.Requisition is { } requisition)
                    UnloadStockroomOrder(requisition, grid, storage, dropoff);

                Finish(worker, $"completed for {Name(order.Crate)} at {Name(order.Dropoff)}");
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

            // A freight joint can stop the worker short of the steering system's door interaction.
            // Reuse the normal door opening checks for doors ahead along the current path.
            var departmentRoute = TryComp(order.Dropoff, out CargoDepartmentPalletComponent? pallet);
            if (order.Pulling && pallet != null && order.NextApproach > 0 &&
                order.NextApproach <= pallet.ApproachOffsets.Count && steering.Status == SteeringStatus.InRange)
            {
                SteerToPallet(worker, order, dropoff, steering, pallet);
                continue;
            }

            if (departmentRoute && order.Pulling)
            {
                foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(crateTransform.Coordinates, 1f, LookupFlags.Uncontained))
                {
                    if (door.Comp.State == DoorState.Open && HasComp<AirlockComponent>(door.Owner) &&
                        door.Comp.NextStateChange != null && _doors.CanOpen(door.Owner, door.Comp, worker))
                        _doors.SetNextStateChange(door.Owner, TimeSpan.FromSeconds(2), door.Comp);
                }
            }
            if (departmentRoute && TryOpenFreightDoor(worker, workerTransform, steering) &&
                steering.Status != SteeringStatus.NoPath)
                continue;

            if (steering.Status == SteeringStatus.NoPath)
            {
                if (departmentRoute && order.NavigationRetries++ < 20)
                {
                    foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(
                                 workerTransform.Coordinates, 2f, LookupFlags.Uncontained))
                    {
                        if (door.Comp.State == DoorState.Closed && _interaction.InRangeAndAccessible(worker, door.Owner))
                            _doors.TryOpen(door.Owner, door.Comp, worker, quiet: true);
                    }

                    var destination = steering.Coordinates;
                    var range = steering.Range;
                    _steering.Unregister(worker);
                    var retry = _steering.Register(worker, destination);
                    retry.Range = range;
                    retry.RepathRange = steering.RepathRange;
                    continue;
                }

                Finish(worker, $"could not reach {Name(order.Dropoff)} from {workerTransform.Coordinates} toward {steering.Coordinates}");
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
            SteerToPallet(worker, order, dropoff, steering, pallet);
        }
    }

    private void SteerToPallet(EntityUid worker, CargoDeliveryComponent order, TransformComponent dropoff,
        NPCSteeringComponent steering, CargoDepartmentPalletComponent? pallet)
    {
        var approach = order.NextApproach++;
        order.NavigationRetries = 0;
        if (pallet != null)
        {
            _steering.Unregister(worker, steering);
            steering = _steering.Register(worker, dropoff.Coordinates);
            steering.RepathRange = 0.3f;
        }
        if (pallet != null && approach < pallet.ApproachOffsets.Count)
        {
            _steering.Register(worker, dropoff.Coordinates.Offset(pallet.ApproachOffsets[approach]), steering).Range = 0.3f;
            return;
        }

        // The worker walks past the pallet so the crate trailing on the pull joint reaches it.
        var direction = dropoff.LocalRotation.RotateVec(Vector2.UnitX);
        _steering.Register(worker, dropoff.Coordinates.Offset(direction * 1.3f), steering).Range = 0.15f;
    }

    private bool TryOpenFreightDoor(EntityUid worker, TransformComponent transform, NPCSteeringComponent steering)
    {
        foreach (var next in steering.CurrentPath.Take(5))
        {
            if (!transform.Coordinates.TryDistance(EntityManager, next.Coordinates, out var distance) ||
                distance > SharedInteractionSystem.InteractionRange)
                break;

            if (!TryComp<MapGridComponent>(next.GraphUid, out var grid))
                continue;

            foreach (var door in _map.GetLocalAnchoredEntities(next.GraphUid, grid, next.Box))
            {
                if (TryComp<DoorComponent>(door, out var component) && component.State == DoorState.Closed &&
                    _interaction.InRangeAndAccessible(worker, door) && _doors.TryOpen(door, component, worker, quiet: true))
                    return true;
            }
        }

        return false;
    }

    private void Finish(EntityUid worker, string reason)
    {
        _work.RecordOutcome(worker, reason, TryComp<CargoDeliveryComponent>(worker, out var order) && order.Completed);
        Log.Info($"Living Station cargo delivery {reason} for {ToPrettyString(worker)}.");
        RemComp<CargoDeliveryComponent>(worker);
    }

    private void OnDeliveryShutdown(EntityUid uid, CargoDeliveryComponent order, ComponentShutdown args)
    {
        if (TryComp(order.Crate, out PullableComponent? crate) && crate.Puller == uid)
            _pulling.TryStopPull(order.Crate, crate, uid);

        if (!order.Completed && order.Requisition is { } request &&
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
            pickup.Workers.Clear();
            pickup.AutomaticHauling = false;
            pickup.NextSteelExport = TimeSpan.Zero;
            pickup.NextOreHaul = TimeSpan.Zero;
            pickup.OreLoaded = false;
            pickup.OreIssue = null;
        }
    }
}
