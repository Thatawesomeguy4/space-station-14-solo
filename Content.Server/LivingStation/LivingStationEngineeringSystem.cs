using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Station.Systems;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationEngineeringSystem : EntitySystem
{
    private static readonly EntProtoId Engineer = "MobLivingStationChiefEngineer";
    internal static readonly TimeSpan RepairTimeout = TimeSpan.FromSeconds(20);
    private const float ApproachRange = 0.35f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private LivingStationIncidentSystem _incidents = default!;
    [Dependency] private SharedPoweredLightSystem _lights = default!;
    [Dependency] private SharedLightBulbSystem _bulbs = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private EntityProviderSystem _provider = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private StationSystem _station = default!;

    internal EntityUid? ChiefEngineer { get; private set; }
    internal EntityUid? Fixture { get; private set; }

    public override void Initialize()
    {
        SubscribeLocalEvent<EngineeringRepairComponent, ComponentShutdown>(OnRepairShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void StartShift(MapId mapId)
    {
        var fixtures = EntityQueryEnumerator<EngineeringRepairFixtureComponent, PoweredLightComponent, TransformComponent>();
        while (fixtures.MoveNext(out var fixture, out var marker, out var light, out var transform))
        {
            if (transform.MapID != mapId || _station.GetOwningStation(fixture, transform) == null ||
                _lights.GetBulb(fixture, light) is not { } bulb || !HasComp<LightBulbComponent>(bulb))
                continue;

            var worker = SpawnAttachedTo(Engineer, transform.Coordinates.Offset(marker.WorkerOffset));
            ChiefEngineer = worker;
            Fixture = fixture;
            _incidents.RegisterChiefEngineer(worker, fixture);
            return;
        }
    }

    internal bool TryStartRepair()
    {
        if (ChiefEngineer is not { } worker || Fixture is not { } fixture ||
            !_mobState.IsAlive(worker) || HasComp<ActorComponent>(worker) ||
            !TryComp<PoweredLightComponent>(fixture, out var light) ||
            !TryComp(_lights.GetBulb(fixture, light), out LightBulbComponent? bulb) ||
            bulb.State != LightBulbState.Normal || !HasReplacement(worker, light.BulbType) ||
            Transform(worker).GridUid is not { } grid || grid != Transform(fixture).GridUid ||
            !_work.TryAssign(worker, new EntityCoordinates(fixture, Vector2.Zero), RepairTimeout))
            return false;

        _bulbs.SetState(bulb.Owner, LightBulbState.Burned);
        _lights.SetState(fixture, light.On, light);
        _incidents.BeginLightIncident();
        AddComp<EngineeringRepairComponent>(worker);
        EnsureComp<ActiveNPCComponent>(worker);
        return true;
    }

    internal void EndRepair()
    {
        if (ChiefEngineer is { } worker)
            RemComp<EngineeringRepairComponent>(worker);
    }

    public override void Update(float frameTime)
    {
        var repairs = EntityQueryEnumerator<EngineeringRepairComponent, TransformComponent>();
        while (repairs.MoveNext(out var worker, out _, out var transform))
        {
            if (!TryComp(worker, out LivingStationWorkClaimComponent? claim) ||
                !_mobState.IsAlive(worker) || HasComp<ActorComponent>(worker) ||
                !TryComp(claim.Target.EntityId, out EngineeringRepairFixtureComponent? marker) ||
                !TryComp(claim.Target.EntityId, out PoweredLightComponent? light) ||
                !TryComp(claim.Target.EntityId, out TransformComponent? fixtureTransform) ||
                transform.GridUid == null || transform.GridUid != fixtureTransform.GridUid)
            {
                Finish(worker, "cancelled because the worker or fixture is unavailable");
                continue;
            }

            var fixture = claim.Target.EntityId;
            if (light.CurrentLit)
            {
                Finish(worker, "completed, fixture lit");
                continue;
            }

            if (_timing.CurTime >= claim.Deadline)
            {
                Finish(worker, "timed out before the fixture was lit");
                continue;
            }

            // A working bulb can be installed while the power is off. Wait for the actual light state.
            if (TryComp(_lights.GetBulb(fixture, light), out LightBulbComponent? bulb) && bulb.State == LightBulbState.Normal)
            {
                _steering.Unregister(worker);
                continue;
            }

            if (!HasReplacement(worker, light.BulbType))
            {
                Finish(worker, "cancelled because no replacement bulb is available");
                continue;
            }

            var approach = fixtureTransform.Coordinates.Offset(marker.ApproachOffset);
            if (transform.Coordinates.TryDistance(EntityManager, approach, out var distance) && distance <= ApproachRange &&
                _interaction.InRangeUnobstructed(worker, fixture))
            {
                _steering.Unregister(worker);
                if (bulb?.State is LightBulbState.Burned or LightBulbState.Broken)
                    _incidents.ObserveBrokenLight(worker, fixture);
                _interaction.UserInteraction(worker, fixtureTransform.Coordinates, fixture);
                if (!TryComp(_lights.GetBulb(fixture, light), out bulb) || bulb.State != LightBulbState.Normal)
                    Finish(worker, "failed to replace the bulb");
                continue;
            }

            if (TryComp(worker, out NPCSteeringComponent? steering))
            {
                if (steering.Status is SteeringStatus.NoPath or SteeringStatus.InRange)
                    Finish(worker, "could not reach the fixture");
            }
            else if (_pathfinding.GetPoly(transform.Coordinates) != null && _pathfinding.GetPoly(approach) != null)
            {
                _steering.Register(worker, approach).Range = ApproachRange;
            }
        }
    }

    private bool HasReplacement(EntityUid worker, LightBulbType type)
    {
        if (!_hands.TryGetActiveItem(worker, out var held) || !TryComp(held, out LightReplacerComponent? tool) ||
            !_provider.TryGetEntityCounter(held.Value, out var stock))
            return false;

        var replacement = type == LightBulbType.Tube ? tool.ActiveLightTube : tool.ActiveLightBulb;
        return stock.TryGetValue(replacement, out var count) && count > 0;
    }

    private void Finish(EntityUid worker, string outcome)
    {
        Log.Info($"Living Station engineering repair {outcome} for {ToPrettyString(worker)}.");
        RemComp<EngineeringRepairComponent>(worker);
    }

    private void OnRepairShutdown(EntityUid worker, EngineeringRepairComponent repair, ComponentShutdown args)
    {
        _work.Release(worker);
        _steering.Unregister(worker);
        RemComp<ActiveNPCComponent>(worker);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var repairs = AllEntityQuery<EngineeringRepairComponent>();
        while (repairs.MoveNext(out var worker, out _))
            RemComp<EngineeringRepairComponent>(worker);
        ChiefEngineer = null;
        Fixture = null;
    }
}
