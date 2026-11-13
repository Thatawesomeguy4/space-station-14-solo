using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Placeable;
using Content.Shared.Popups;
using Content.Shared.Station.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationBarSystem : EntitySystem
{
    private static readonly EntProtoId Bartender = "MobLivingStationBartender";
    private static readonly EntProtoId WaterBottle = "DrinkWaterBottleFull";
    private static readonly ProtoId<ReagentPrototype> Water = "Water";
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(20);
    private const float WorkRange = 0.35f;
    private const float SurfaceHalfWidth = 0.5f;
    private const float SurfaceLookupRange = 0.71f;

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private StationSystem _station = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<BarServiceCounterComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<BarServiceOrderComponent, ComponentShutdown>(OnOrderShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void StartShift(MapId mapId)
    {
        var counters = EntityQueryEnumerator<BarServiceCounterComponent, PlaceableSurfaceComponent, TransformComponent>();
        while (counters.MoveNext(out var uid, out var counter, out var surface, out var transform))
        {
            if (transform.MapID != mapId || !surface.IsPlaceable || _station.GetOwningStation(uid, transform) == null)
                continue;

            counter.Bartender = SpawnAttachedTo(Bartender, transform.Coordinates.Offset(counter.StaffOffset));
            SpawnAttachedTo(WaterBottle, transform.Coordinates.Offset(counter.StockOffset));
            return;
        }
    }

    private void OnGetVerbs(Entity<BarServiceCounterComponent> counter, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !CanRequest(counter, args.User))
            return;

        var customer = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-bar-request-water"),
            Act = () => RequestWater(counter, customer),
        });
    }

    private bool CanRequest(Entity<BarServiceCounterComponent> counter, EntityUid customer)
    {
        if (_ticker.RunLevel != GameRunLevel.InRound || !_config.GetCVar(CCVars.LivingStationEnabled) ||
            counter.Comp.Bartender == null || counter.Comp.Bartender == customer || !_mobState.IsAlive(customer) ||
            !TryComp(customer, out TransformComponent? customerTransform) ||
            !TryComp(counter, out TransformComponent? counterTransform) ||
            counterTransform.GridUid == null || counterTransform.GridUid != customerTransform.GridUid)
            return false;

        var center = _transform.GetWorldPosition(counterTransform);
        var staff = _transform.ToMapCoordinates(counterTransform.Coordinates.Offset(counter.Comp.StaffOffset)).Position;
        return Vector2.Dot(_transform.GetWorldPosition(customerTransform) - center, staff - center) < 0;
    }

    private void RequestWater(Entity<BarServiceCounterComponent> counter, EntityUid customer)
    {
        if (!CanRequest(counter, customer) || !_blocker.CanInteract(customer, counter) ||
            !_interaction.InRangeAndAccessible(customer, counter.Owner) ||
            !TryComp(counter, out PlaceableSurfaceComponent? surface) || !surface.IsPlaceable)
            return;

        var coordinates = Transform(counter).Coordinates;
        if (FindWaterAt(coordinates) != null)
        {
            _popup.PopupEntity(Loc.GetString("living-station-bar-water-ready"), counter, customer);
            return;
        }

        var worker = counter.Comp.Bartender!.Value;
        if (_mobState.IsAlive(worker) && !HasComp<ActorComponent>(worker) &&
            Transform(worker).GridUid == Transform(counter).GridUid)
        {
            var held = _hands.GetActiveItem(worker);
            var drink = held == null ? FindWaterAt(coordinates.Offset(counter.Comp.StockOffset)) : held;
            if (drink is { } bottle && IsWater(bottle) &&
                _work.TryAssign(worker, new EntityCoordinates(counter, Vector2.Zero), OrderTimeout))
            {
                AddComp<BarServiceOrderComponent>(worker).Drink = bottle;
                EnsureComp<ActiveNPCComponent>(worker);
                _popup.PopupEntity(Loc.GetString("living-station-bar-order-accepted"), counter, customer);
                return;
            }
        }

        _popup.PopupEntity(Loc.GetString("living-station-bar-order-unavailable"), counter, customer);
    }

    public override void Update(float frameTime)
    {
        var orders = EntityQueryEnumerator<BarServiceOrderComponent, TransformComponent>();
        while (orders.MoveNext(out var worker, out var order, out var workerTransform))
        {
            if (!TryComp(worker, out LivingStationWorkClaimComponent? claim) || !_mobState.IsAlive(worker) ||
                HasComp<ActorComponent>(worker) || !TryComp(claim.Target.EntityId, out BarServiceCounterComponent? counter) ||
                !TryComp(claim.Target.EntityId, out PlaceableSurfaceComponent? surface) || !surface.IsPlaceable ||
                !TryComp(claim.Target.EntityId, out TransformComponent? counterTransform) ||
                workerTransform.GridUid == null || workerTransform.GridUid != counterTransform.GridUid)
            {
                Finish(worker, "cancelled because the bartender or counter is unavailable");
                continue;
            }

            var counterLocation = counterTransform.Coordinates;
            if (FindWaterAt(counterLocation) != null)
            {
                Finish(worker, "completed, water is on the counter");
                continue;
            }

            if (_timing.CurTime >= claim.Deadline)
            {
                Finish(worker, "timed out");
                continue;
            }

            var held = _hands.GetActiveItem(worker);
            var carrying = held == order.Drink;
            if (!IsWater(order.Drink) || !carrying &&
                (held != null || !IsAt(order.Drink, counterLocation.Offset(counter.StockOffset))))
            {
                Finish(worker, "cancelled because the bottle is unavailable");
                continue;
            }

            var stand = counterLocation.Offset(carrying ? counter.StaffOffset : counter.StockApproachOffset);
            var interactionTarget = carrying ? claim.Target.EntityId : order.Drink;
            if (workerTransform.Coordinates.TryDistance(EntityManager, stand, out var distance) && distance <= WorkRange &&
                _interaction.InRangeAndAccessible(worker, interactionTarget))
            {
                _steering.Unregister(worker);
                if (carrying)
                {
                    // Use the normal targeted drop so the sealed bottle can be placed as-is.
                    var served = _hands.TryDrop(worker, order.Drink, counterLocation) && IsAt(order.Drink, counterLocation);
                    Finish(worker, served ? "completed, water is on the counter" : "could not place water on the counter");
                }
                else
                {
                    _interaction.UserInteraction(worker, Transform(order.Drink).Coordinates, order.Drink);
                    if (_hands.GetActiveItem(worker) != order.Drink)
                        Finish(worker, "could not pick up the bottle");
                }

                continue;
            }

            if (TryComp(worker, out NPCSteeringComponent? steering))
            {
                if (steering.Status is SteeringStatus.NoPath or SteeringStatus.InRange)
                    Finish(worker, "could not reach the stock or counter");
            }
            else if (_pathfinding.GetPoly(workerTransform.Coordinates) != null && _pathfinding.GetPoly(stand) != null)
            {
                _steering.Register(worker, stand).Range = WorkRange;
            }
        }
    }

    private EntityUid? FindWaterAt(EntityCoordinates coordinates)
    {
        foreach (var uid in _lookup.GetEntitiesInRange(coordinates, SurfaceLookupRange, LookupFlags.Uncontained))
        {
            if (IsWater(uid) && IsAt(uid, coordinates))
                return uid;
        }

        return null;
    }

    private bool IsWater(EntityUid uid) =>
        TryPrototype(uid, out var prototype) && prototype.ID == WaterBottle.Id &&
        _solutions.TryGetSolution(uid, "drink", out _, out var solution) && solution.Volume > 0 &&
        solution.GetTotalPrototypeQuantity(Water) == solution.Volume;

    private bool IsAt(EntityUid uid, EntityCoordinates coordinates)
    {
        if (!TryComp(uid, out TransformComponent? transform) || transform.ParentUid != coordinates.EntityId)
            return false;

        var offset = Vector2.Abs(transform.Coordinates.Position - coordinates.Position);
        return offset.X < SurfaceHalfWidth && offset.Y < SurfaceHalfWidth;
    }

    private void Finish(EntityUid worker, string outcome)
    {
        Log.Info($"Living Station bar order {outcome} for {ToPrettyString(worker)}.");
        RemComp<BarServiceOrderComponent>(worker);
    }

    private void OnOrderShutdown(EntityUid worker, BarServiceOrderComponent order, ComponentShutdown args)
    {
        _work.Release(worker);
        _steering.Unregister(worker);
        RemComp<ActiveNPCComponent>(worker);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var orders = AllEntityQuery<BarServiceOrderComponent>();
        while (orders.MoveNext(out var worker, out _))
            RemComp<BarServiceOrderComponent>(worker);
    }
}
