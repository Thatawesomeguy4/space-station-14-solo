using Content.Server.GameTicking;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Content.Shared.Spawners.Components;
using Content.Shared.Station.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

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

    public override void Initialize()
    {
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old != GameRunLevel.PreRoundLobby || ev.New != GameRunLevel.InRound ||
            !_config.GetCVar(CCVars.LivingStationEnabled))
            return;

        SpawnJanitor();
        _cargo.StartDelivery(_ticker.DefaultMap);
        _security.StartPatrol(_ticker.DefaultMap);
        _engineering.StartShift(_ticker.DefaultMap);
        _medical.StartTreatment(_ticker.DefaultMap);
        _bar.StartShift(_ticker.DefaultMap);
    }

    private void SpawnJanitor()
    {
        var markers = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var marker, out var transform))
        {
            if (marker.SpawnType != SpawnPointType.Job || marker.Job != JanitorJob ||
                transform.MapID != _ticker.DefaultMap || _station.GetOwningStation(uid, transform) == null)
                continue;

            var janitor = SpawnAttachedTo(Janitor, transform.Coordinates);
            Log.Info($"Spawned Living Station janitor {ToPrettyString(janitor)} for round {_ticker.RoundId}.");
            return;
        }

        Log.Warning("Living Station could not find a janitor job spawn point on the main station map.");
    }
}
