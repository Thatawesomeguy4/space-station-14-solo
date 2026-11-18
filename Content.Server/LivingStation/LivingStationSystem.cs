using System.Linq;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Content.Shared.Spawners.Components;
using Content.Shared.Station.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationSystem : EntitySystem
{
    private static readonly EntProtoId Janitor = "MobLivingStationJanitor";
    private static readonly ProtoId<JobPrototype> JanitorJob = "Janitor";

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private LivingStationCargoSystem _cargo = default!;
    [Dependency] private LivingStationEngineeringSystem _engineering = default!;
    [Dependency] private LivingStationMedicalSystem _medical = default!;
    [Dependency] private LivingStationSecuritySystem _security = default!;
    [Dependency] private LivingStationBarSystem _bar = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LivingStationIdleSystem _idle = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;

    private readonly Dictionary<EntityUid, LivingStationDepartment> _crew = new();
    internal IReadOnlyDictionary<EntityUid, LivingStationDepartment> Crew => _crew;
    private int[]? _requested;
    private int[]? _spawned;
    private TimeSpan _nextStaffingCheck;

    public override void Initialize()
    {
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        Subs.CVar(_config, CCVars.LivingStationEnabled, enabled =>
            _config.SetCVar(CCVars.LivingStationStaffingAvailable, enabled), true);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound)
        {
            _crew.Clear();
            _requested = null;
            _spawned = null;
            _config.SetCVar(CCVars.LivingStationStaffingStatus, "");
            return;
        }

        _work.ResetCounters();
        if (ev.Old != GameRunLevel.PreRoundLobby ||
            !_config.GetCVar(CCVars.LivingStationEnabled))
            return;

        _requested = LivingStationStaffing.Departments.Select(Count).ToArray();
        var map = _ticker.DefaultMap;
        var service = _requested[(int) LivingStationDepartment.Service];
        var bar = service >= 2 && _bar.StartShift(map) ? 1 : 0;
        var personnel = service >= 3 ? SpawnDepartmentHead("HeadOfPersonnel", "MobLivingStationHeadOfPersonnel",
            "Head of Personnel", LivingStationDepartment.Service) : 0;
        _spawned =
        [
            bar + personnel + SpawnJanitors(service - bar - personnel),
            _cargo.StartDelivery(map, _requested[(int) LivingStationDepartment.Cargo]),
            _engineering.StartShift(map, _requested[(int) LivingStationDepartment.Engineering]),
            _medical.StartTreatment(map, _requested[(int) LivingStationDepartment.Medical]),
            _security.StartPatrol(map, _requested[(int) LivingStationDepartment.Security]),
            _requested[(int) LivingStationDepartment.Science] > 0
                ? SpawnDepartmentHead("ResearchDirector", "MobLivingStationResearchDirector", "Research Director", LivingStationDepartment.Science)
                : 0,
        ];
        PublishStaffing();
    }

    public override void Update(float frameTime)
    {
        if (_requested != null && _timing.CurTime >= _nextStaffingCheck)
            PublishStaffing();
    }

    private void PublishStaffing()
    {
        _nextStaffingCheck = _timing.CurTime + TimeSpan.FromSeconds(1);
        var active = new int[LivingStationStaffing.Departments.Count];
        foreach (var (worker, department) in _crew)
        {
            if (!TerminatingOrDeleted(worker) && _mobState.IsAlive(worker) && !HasComp<ActorComponent>(worker))
                active[(int) department]++;
        }

        var status = string.Join(",", active) + "|" + string.Join(",", _spawned!) + "|" + string.Join(",", _requested!);
        if (_config.GetCVar(CCVars.LivingStationStaffingStatus) != status)
            _config.SetCVar(CCVars.LivingStationStaffingStatus, status);
    }

    private int Count(CVarDef<int> setting) => Math.Clamp(_config.GetCVar(setting), 0, CCVars.LivingStationMaxDepartmentStaff);

    internal void RegisterCrew(EntityUid worker, LivingStationDepartment department)
    {
        _crew.Add(worker, department);
        EntityManager.System<LivingStationBartenderDialogueSystem>().RegisterConversationPartner(worker, department);
        if (department != LivingStationDepartment.Security)
            _idle.Register(worker, department);
    }

    private int SpawnDepartmentHead(ProtoId<JobPrototype> job, EntProtoId prototype, string title, LivingStationDepartment department)
    {
        var markers = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var marker, out var transform))
        {
            if (marker.SpawnType != SpawnPointType.Job || marker.Job != job ||
                transform.MapID != _ticker.DefaultMap || _station.GetOwningStation(uid, transform) == null)
                continue;

            var worker = SpawnAttachedTo(prototype, transform.Coordinates);
            RegisterCrew(worker, department);
            EntityManager.System<LivingStationIncidentSystem>().RegisterDepartmentHead(title, worker);
            return 1;
        }

        return 0;
    }

    internal int SpawnSupportCrew(LivingStationDepartment department, EntProtoId prototype, EntityCoordinates origin,
        int count, List<EntityUid>? workers = null)
    {
        if (count <= 0 || origin.EntityId == EntityUid.Invalid || !TryComp(origin.EntityId, out MapGridComponent? grid))
            return 0;

        var spawned = 0;
        var mapOrigin = _transform.ToMapCoordinates(origin);
        for (var radius = 0; radius <= 4 && spawned < count; radius++)
        {
            for (var x = -radius; x <= radius && spawned < count; x++)
            for (var y = -radius; y <= radius && spawned < count; y++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                    continue;

                var location = origin.Offset(new Vector2(x, y));
                if (_map.GetTileRef(origin.EntityId, grid, location).Tile.IsEmpty ||
                    !_interaction.InRangeUnobstructed(mapOrigin, _transform.ToMapCoordinates(location), 6f) ||
                    _lookup.GetEntitiesInRange<MobStateComponent>(location, 0.65f, LookupFlags.Uncontained).Count != 0)
                    continue;

                var worker = SpawnAttachedTo(prototype, location);
                RegisterCrew(worker, department);
                workers?.Add(worker);
                spawned++;
            }
        }

        return spawned;
    }

    private int SpawnJanitors(int count)
    {
        if (count == 0)
            return 0;

        var markers = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var marker, out var transform))
        {
            if (marker.SpawnType != SpawnPointType.Job || marker.Job != JanitorJob ||
                transform.MapID != _ticker.DefaultMap || _station.GetOwningStation(uid, transform) == null)
                continue;

            var janitor = SpawnAttachedTo(Janitor, transform.Coordinates);
            RegisterCrew(janitor, LivingStationDepartment.Service);
            Log.Info($"Spawned Living Station janitor {ToPrettyString(janitor)} for round {_ticker.RoundId}.");
            return 1 + SpawnSupportCrew(LivingStationDepartment.Service, Janitor, transform.Coordinates, count - 1);
        }

        Log.Warning("Living Station could not find a janitor job spawn point on the main station map.");
        return 0;
    }
}
