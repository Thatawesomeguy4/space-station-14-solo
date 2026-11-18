using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class LivingStationIdleRouteComponent : Component
{
    public LivingStationDepartment Department;
    public EntityCoordinates Home;
    public TimeSpan NextWalk;
    public bool Walking;
    public int NextDirection;
    public TimeSpan NextBreak;
    public TimeSpan NextStockroom;
    public bool ReturnHome;
    public EntityCoordinates Destination;
    public LivingStationRoutine Activity;
}

public enum LivingStationRoutine { DepartmentRounds, StockroomVisit, BarBreak, ReturningHome }

public sealed partial class LivingStationIdleSystem : EntitySystem
{
    private static readonly Vector2[] Directions = [Vector2.UnitY, Vector2.UnitX, -Vector2.UnitY, -Vector2.UnitX];

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;

    internal void Register(EntityUid worker, LivingStationDepartment department)
    {
        // Security and janitorial already select their own idle movement.
        if (TryComp<HTNComponent>(worker, out var htn) && htn.Enabled)
            return;

        var route = AddComp<LivingStationIdleRouteComponent>(worker);
        route.Home = Transform(worker).Coordinates;
        route.Department = department;
        route.NextWalk = _timing.CurTime + TimeSpan.FromSeconds(25 + (int) worker % 10);
        route.NextBreak = _timing.CurTime + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds((int) worker % 45);
        route.NextStockroom = _timing.CurTime + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds((int) worker % 30);
        route.NextDirection = (int) worker % Directions.Length;
    }

    internal void Stop(EntityUid worker)
    {
        if (!TryComp<LivingStationIdleRouteComponent>(worker, out var route))
            return;

        if (route.Walking)
        {
            _steering.Unregister(worker);
            RemComp<ActiveNPCComponent>(worker);
            route.Walking = false;
        }

        route.NextWalk = _timing.CurTime + TimeSpan.FromSeconds(12);
        route.Activity = LivingStationRoutine.ReturningHome;
        route.ReturnHome = true;
    }

    public override void Update(float frameTime)
    {
        if (_ticker.RunLevel != GameRunLevel.InRound)
            return;

        var enabled = _config.GetCVar(CCVars.LivingStationEnabled);
        var routes = EntityQueryEnumerator<LivingStationIdleRouteComponent, TransformComponent>();
        while (routes.MoveNext(out var worker, out var route, out var transform))
        {
            if (!enabled || !_mobs.IsAlive(worker) || HasComp<ActorComponent>(worker) ||
                transform.ParentUid != route.Home.EntityId || HasComp<LivingStationWorkClaimComponent>(worker))
            {
                if (route.Walking)
                    Stop(worker);
                continue;
            }

            if (route.Walking)
            {
                if (_timing.CurTime >= route.NextWalk || !TryComp<NPCSteeringComponent>(worker, out var steering) ||
                    steering.Status != SteeringStatus.Moving)
                {
                    var arrived = transform.Coordinates.TryDistance(EntityManager, route.Destination, out var remaining) && remaining < 0.7f;
                    var visit = route.Activity is LivingStationRoutine.StockroomVisit or LivingStationRoutine.BarBreak;
                    var activity = route.Activity;
                    Stop(worker);
                    if (visit && arrived)
                    {
                        route.Activity = activity;
                        route.NextWalk = _timing.CurTime + TimeSpan.FromSeconds(20);
                    }
                    else if (arrived && activity == LivingStationRoutine.ReturningHome)
                    {
                        route.Activity = LivingStationRoutine.DepartmentRounds;
                        route.ReturnHome = false;
                    }
                }
                continue;
            }

            if (_timing.CurTime < route.NextWalk || HasComp<NPCSteeringComponent>(worker) ||
                !TryComp<MapGridComponent>(route.Home.EntityId, out var grid))
                continue;

            route.NextWalk = _timing.CurTime + TimeSpan.FromSeconds(12);
            var target = route.Home;
            if (!route.ReturnHome && TryVisit(worker, route, out var visitTarget))
            {
                target = visitTarget;
            }
            else if (route.ReturnHome)
            {
                route.Activity = LivingStationRoutine.ReturningHome;
            }
            else if (transform.Coordinates.TryDistance(EntityManager, target, out var distance) && distance < 0.6f)
            {
                target = target.Offset(Directions[route.NextDirection] * 2);
                route.NextDirection = (route.NextDirection + 1) % Directions.Length;
                if (!_interaction.InRangeUnobstructed(worker, target, 2.7f))
                    continue;
            }

            if (_map.GetTileRef(route.Home.EntityId, grid, target).Tile.IsEmpty || _pathfinding.GetPoly(target) == null ||
                _lookup.GetEntitiesInRange<MobStateComponent>(target, 0.65f, LookupFlags.Uncontained).Count > 0)
                continue;

            route.Walking = true;
            route.Destination = target;
            route.NextWalk = _timing.CurTime + TimeSpan.FromSeconds(45);
            EnsureComp<ActiveNPCComponent>(worker);
            _steering.Register(worker, target).Range = 0.35f;
        }
    }

    private bool TryVisit(EntityUid worker, LivingStationIdleRouteComponent route, out EntityCoordinates target)
    {
        target = default;
        if (_timing.CurTime >= route.NextBreak)
        {
            route.NextBreak = _timing.CurTime + TimeSpan.FromSeconds(30);
            var bars = EntityQueryEnumerator<BarServiceCounterComponent, TransformComponent>();
            while (bars.MoveNext(out _, out var bar, out var position))
            {
                if (bar.Bartender != worker && position.GridUid == Transform(worker).GridUid &&
                    TryVisitFloor(position.Coordinates.Offset(-bar.StaffOffset), out target))
                {
                    route.Activity = LivingStationRoutine.BarBreak;
                    route.NextBreak = _timing.CurTime + TimeSpan.FromMinutes(5);
                    return true;
                }
            }
        }

        if (_timing.CurTime < route.NextStockroom)
            return false;

        route.NextStockroom = _timing.CurTime + TimeSpan.FromMinutes(2);
        var rooms = EntityQueryEnumerator<CargoStockroomComponent, TransformComponent>();
        while (rooms.MoveNext(out _, out var stockroom, out var position))
        {
            var matches = route.Department switch
            {
                LivingStationDepartment.Engineering => stockroom.Department == CargoDepartment.Engineering,
                LivingStationDepartment.Medical => stockroom.Department == CargoDepartment.Medical,
                LivingStationDepartment.Service => stockroom.Department == CargoDepartment.Janitorial,
                _ => false,
            };
            if (matches && position.GridUid == Transform(worker).GridUid && TryVisitFloor(position.Coordinates, out target))
            {
                route.Activity = LivingStationRoutine.StockroomVisit;
                return true;
            }
        }

        return false;
    }

    private bool TryVisitFloor(EntityCoordinates origin, out EntityCoordinates target)
    {
        foreach (var direction in Directions)
        {
            target = origin.Offset(direction);
            if (_pathfinding.GetPoly(target) is { } poly && poly.IsValid() &&
                _lookup.GetEntitiesInRange<MobStateComponent>(target, 0.65f, LookupFlags.Uncontained).Count == 0)
                return true;
        }
        target = default;
        return false;
    }
}
