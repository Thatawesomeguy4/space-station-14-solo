using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.RoundEnd;
using Content.Shared.AlertLevel;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Light.Components;
using Content.Shared.Station.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationStoryDirectorSystem : EntitySystem
{
    internal static readonly TimeSpan Warmup = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan StartInterval = TimeSpan.FromMinutes(8);
    internal static readonly TimeSpan RumorLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ChaoticRumorLifetime = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private IGameMapManager _maps = default!;
    [Dependency] private RoundEndSystem _roundEnd = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private AlertLevelSystem _alerts = default!;
    [Dependency] private LivingStationEngineeringSystem _engineering = default!;
    [Dependency] private LivingStationIncidentSystem _incidents = default!;

    internal bool MaintenanceActive => _repairExpires != null;
    internal bool RumorActive => _rumorExpires != null && !_rumorVerified;
    private StoryIncident _next;
    private TimeSpan _eligibleAt;
    private TimeSpan _nextCheck;
    private TimeSpan? _lastStart;
    private TimeSpan? _repairExpires;
    private TimeSpan? _rumorExpires;
    private bool _rumorVerified;

    public override void Initialize()
    {
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        _repairExpires = null;
        _rumorExpires = null;
        _rumorVerified = false;
        _lastStart = null;
        _next = StoryIncident.Maintenance;
        _eligibleAt = _timing.CurTime + Warmup;
        _nextCheck = _eligibleAt;
    }

    public override void Update(float frameTime) => Advance(_timing.CurTime);

    internal void Advance(TimeSpan now)
    {
        if (now < _nextCheck || _ticker.RunLevel != GameRunLevel.InRound || _maps.GetSelectedMap()?.ID != "Saltern")
            return;

        _nextCheck = now + CheckInterval;
        if (_rumorExpires is { } rumorExpires)
        {
            if (!_rumorVerified && _incidents.RumorVerified)
            {
                _rumorVerified = true;
                Log.Info("Living Station director verified Rumor.");
            }

            if (now >= rumorExpires)
            {
                if (!_rumorVerified)
                    Log.Info("Living Station director expired Rumor.");

                _incidents.EndRumor();
                _rumorExpires = null;
            }
        }

        if (_repairExpires is { } repairExpires)
        {
            if (TryComp<PoweredLightComponent>(_engineering.Fixture, out var light) && light.CurrentLit)
                FinishRepair("resolved");
            else if (now >= repairExpires)
                FinishRepair("expired");
        }

        if (now < _eligibleAt || !_config.GetCVar(CCVars.LivingStationEnabled) ||
            !_config.GetCVar(CCVars.LivingStationDirectorEnabled))
            return;

        var (capacity, interval, rumorLifetime) = _config.GetCVar(CCVars.LivingStationDirectorPacing).Trim().ToLowerInvariant() switch
        {
            "quiet" => (1, TimeSpan.FromMinutes(12), RumorLifetime),
            "chaotic" => (2, TimeSpan.FromMinutes(5), ChaoticRumorLifetime),
            _ => (1, StartInterval, RumorLifetime),
        };
        var activeCount = (MaintenanceActive ? 1 : 0) + (RumorActive ? 1 : 0);
        if (activeCount >= capacity || _lastStart is { } lastStart && now - lastStart < interval)
            return;

        if (_roundEnd.IsRoundEndRequested() || _engineering.Fixture is not { } fixture ||
            _station.GetOwningStation(fixture) is not { } station ||
            _alerts.TryGetLevel(station, out var alert) && alert.Value.Id is not ("Green" or "Blue"))
        {
            Defer(now, "station unavailable, alert elevated, or evacuation requested");
            return;
        }

        var started = _next == StoryIncident.Maintenance
            ? !MaintenanceActive && _engineering.TryStartRepair()
            : _rumorExpires == null && _incidents.TryStartRumor();
        if (!started)
        {
            // A depleted repair tool should not starve social incidents.
            Defer(now, "incident target or responder unavailable");
            _next = _next == StoryIncident.Maintenance ? StoryIncident.Rumor : StoryIncident.Maintenance;
            return;
        }

        _lastStart = now;
        if (_next == StoryIncident.Rumor)
        {
            _rumorVerified = false;
            _rumorExpires = now + rumorLifetime;
        }
        else
        {
            _repairExpires = now + LivingStationEngineeringSystem.RepairTimeout;
        }

        Log.Info($"Living Station director started {_next}.");
        _next = _next == StoryIncident.Maintenance ? StoryIncident.Rumor : StoryIncident.Maintenance;
    }

    private void Defer(TimeSpan now, string reason)
    {
        Log.Debug($"Living Station director deferred {_next}: {reason}.");
        _eligibleAt = now + RetryInterval;
    }

    private void FinishRepair(string outcome)
    {
        _engineering.EndRepair();
        _repairExpires = null;
        Log.Info($"Living Station director {outcome} Maintenance.");
    }
}

internal enum StoryIncident
{
    Maintenance,
    Rumor,
}
