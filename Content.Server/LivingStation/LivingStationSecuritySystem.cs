using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Station.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationSecuritySystem : EntitySystem
{
    private static readonly EntProtoId OfficerPrototype = "MobLivingStationHeadOfSecurity";
    private static readonly TimeSpan InvestigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PatrolPause = TimeSpan.FromSeconds(1);
    private const float ArrivalRange = 0.4f;

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private LivingStationIncidentSystem _incidents = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private StationSystem _station = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SecurityPatrolComponent, ComponentShutdown>(OnPatrolShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void StartPatrol(MapId mapId)
    {
        var points = new List<(int Order, EntityUid Marker)>();
        var markers = EntityQueryEnumerator<SecurityPatrolPointComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var point, out var transform))
        {
            if (transform.MapID == mapId && _station.GetOwningStation(uid, transform) != null)
                points.Add((point.Order, uid));
        }

        if (points.Count < 2)
            return;

        points.Sort();
        var start = Transform(points[0].Marker);
        var route = new List<EntityUid>();
        foreach (var (_, marker) in points)
        {
            if (Transform(marker).GridUid == start.GridUid)
                route.Add(marker);
        }

        if (route.Count < 2)
            return;

        var worker = SpawnAttachedTo(OfficerPrototype, start.Coordinates);
        var patrol = AddComp<SecurityPatrolComponent>(worker);
        patrol.Waypoints = route;
        patrol.NextWaypoint = 1;
        _incidents.RegisterHeadOfSecurity(worker);
    }

    public bool TryReport(EntityUid reporter)
    {
        if (_ticker.RunLevel != GameRunLevel.InRound || !_config.GetCVar(CCVars.LivingStationEnabled) ||
            !_mobState.IsAlive(reporter) || !TryComp(reporter, out TransformComponent? reporterTransform) ||
            reporterTransform.GridUid is not { } grid || !TryComp(grid, out MapGridComponent? mapGrid))
            return false;

        var tile = _map.GetTileRef(grid, mapGrid, reporterTransform.Coordinates);
        if (tile.Tile.IsEmpty)
            return false;

        var target = _map.GridTileToLocal(grid, mapGrid, tile.GridIndices);
        var officers = EntityQueryEnumerator<SecurityPatrolComponent, TransformComponent>();
        while (officers.MoveNext(out var officer, out _, out var transform))
        {
            if (transform.GridUid != grid || !_mobState.IsAlive(officer) || HasComp<ActorComponent>(officer) ||
                !_work.TryAssign(officer, target, InvestigationTimeout))
                continue;

            _steering.Unregister(officer);
            Log.Info($"Living Station security assigned {ToPrettyString(officer)} to inspect {target}.");
            return true;
        }

        return false;
    }

    public override void Update(float frameTime)
    {
        var officers = EntityQueryEnumerator<SecurityPatrolComponent, TransformComponent>();
        while (officers.MoveNext(out var officer, out var patrol, out var transform))
        {
            if (!_mobState.IsAlive(officer) || HasComp<ActorComponent>(officer))
            {
                if (HasComp<LivingStationWorkClaimComponent>(officer))
                    FinishInvestigation(officer, "cancelled");
                _steering.Unregister(officer);
                continue;
            }

            TryComp(officer, out NPCSteeringComponent? steering);
            if (TryComp(officer, out LivingStationWorkClaimComponent? claim))
            {
                if (_timing.CurTime >= claim.Deadline || transform.GridUid != claim.Target.EntityId ||
                    steering?.Status == SteeringStatus.NoPath)
                    FinishInvestigation(officer, "failed to reach the reported location");
                else if (Reached(officer, transform, claim.Target))
                {
                    patrol.LastInspected = claim.Target;
                    FinishInvestigation(officer, $"inspected {claim.Target}");
                }
                else
                    WalkTo(officer, transform.Coordinates, claim.Target, steering);

                continue;
            }

            if (_timing.CurTime < patrol.ResumePatrolAt)
                continue;

            if (!TryComp(patrol.Waypoints[patrol.NextWaypoint], out TransformComponent? waypoint) ||
                waypoint.GridUid != transform.GridUid || steering?.Status == SteeringStatus.NoPath ||
                Reached(officer, transform, waypoint.Coordinates))
            {
                _steering.Unregister(officer);
                patrol.NextWaypoint = (patrol.NextWaypoint + 1) % patrol.Waypoints.Count;
                patrol.ResumePatrolAt = _timing.CurTime + PatrolPause;
                continue;
            }

            WalkTo(officer, transform.Coordinates, waypoint.Coordinates, steering);
        }
    }

    private bool Reached(EntityUid officer, TransformComponent transform, EntityCoordinates target) =>
        transform.Coordinates.TryDistance(EntityManager, target, out var distance) && distance <= ArrivalRange &&
        _interaction.InRangeUnobstructed(officer, target, ArrivalRange);

    private void WalkTo(EntityUid officer, EntityCoordinates start, EntityCoordinates target, NPCSteeringComponent? steering)
    {
        if (steering == null && _pathfinding.GetPoly(start) != null && _pathfinding.GetPoly(target) != null)
            _steering.Register(officer, target).Range = ArrivalRange;
    }

    private void FinishInvestigation(EntityUid officer, string outcome)
    {
        _work.Release(officer);
        _steering.Unregister(officer);
        Log.Info($"Living Station security {outcome} for {ToPrettyString(officer)}.");
    }

    private void OnPatrolShutdown(EntityUid officer, SecurityPatrolComponent patrol, ComponentShutdown args)
    {
        _work.Release(officer);
        _steering.Unregister(officer);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var patrols = AllEntityQuery<SecurityPatrolComponent>();
        while (patrols.MoveNext(out var officer, out _))
            RemComp<SecurityPatrolComponent>(officer);
    }
}
