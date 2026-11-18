using System.Linq;
using System.Numerics;
using Content.Server.NPC.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Light.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Content.Shared.Repairable;
using Content.Shared.Storage.Components;
using Content.Shared.Tools.Components;
using Content.Shared.Verbs;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Localization;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationEngineeringSystem
{
    internal bool CanRespondToCargoFault(EntityUid target) =>
        _engineers.Any(worker => CanWorkMaintenance(worker) && Transform(worker).GridUid == Transform(target).GridUid &&
                                 !HasComp<LivingStationWorkClaimComponent>(worker));

    internal void ReportCargoFault(EntityUid target, EntityUid worker)
    {
        if (_maintenanceRequests.All(order => order.Target != target) &&
            !_engineers.Any(worker => TryComp<LivingStationWorkClaimComponent>(worker, out var claim) && claim.Target.EntityId == target))
            _maintenanceRequests.Insert(0, (target, worker));
    }

    private static readonly TimeSpan MaintenanceTimeout = TimeSpan.FromSeconds(45);
    private readonly List<(EntityUid Target, EntityUid Requester)> _maintenanceRequests = new();
    private readonly Dictionary<EntityUid, TimeSpan> _retryMaintenance = new();
    private TimeSpan _nextMaintenance;
    private readonly Dictionary<EntityUid, RecentRepairRequest> _recentRepairs = new();

    internal IEnumerable<string> ReadQueuedWork() => _maintenanceRequests.Select(order =>
        $"  Engineering: target={order.Target}, requester={order.Requester}; waiting for a capable worker and target reservation").ToArray();

    private sealed record RecentRepairRequest(EntityUid Target, string TargetName, NetUserId? User,
        LocId Status, TimeSpan Expires, EntityUid? Worker = null);

    internal string? GetRecentRepairStatus(EntityUid requester)
    {
        if (!_recentRepairs.TryGetValue(requester, out var request) || request.Expires <= _timing.CurTime ||
            request.User != (CompOrNull<ActorComponent>(requester)?.PlayerSession.UserId))
            return null;

        if (request.Worker is { } worker && TryComp<EngineeringRepairComponent>(worker, out var order) &&
            order.Requester == requester &&
            TryComp<LivingStationWorkClaimComponent>(worker, out var claim) && claim.Target.EntityId == request.Target)
        {
            return Loc.GetString("living-station-engineering-work-active",
                ("target", request.TargetName), ("stage", Loc.GetString(order.Stage)));
        }

        return Loc.GetString(request.Status, ("target", request.TargetName));
    }

    private void RememberRepairRequest(EntityUid requester, EntityUid target)
    {
        _recentRepairs[requester] = new(target, Name(target), CompOrNull<ActorComponent>(requester)?.PlayerSession.UserId,
            "living-station-engineering-followup-queued", _timing.CurTime + TimeSpan.FromMinutes(10));
    }

    private void UpdateRepairMemory(EntityUid requester, EntityUid target, LocId status, EntityUid? worker = null)
    {
        if (_recentRepairs.TryGetValue(requester, out var request) && request.Target == target)
            _recentRepairs[requester] = request with { Status = status, Worker = worker, Expires = _timing.CurTime + TimeSpan.FromMinutes(10) };
    }

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ItemToggleSystem _toggle = default!;
    [Dependency] private RepairableSystem _repairable = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedTransformSystem _transforms = default!;

    private void InitializeMaintenance()
    {
        SubscribeLocalEvent<RepairableComponent, GetVerbsEvent<Verb>>(OnGetEquipmentRepairVerb);
        SubscribeLocalEvent<PoweredLightComponent, GetVerbsEvent<Verb>>(OnGetLightRepairVerb);
        InitializeInfrastructure();
    }

    private void OnGetEquipmentRepairVerb(Entity<RepairableComponent> target, ref GetVerbsEvent<Verb> args) =>
        AddRepairVerb(target, ref args);

    private void OnGetLightRepairVerb(Entity<PoweredLightComponent> target, ref GetVerbsEvent<Verb> args) =>
        AddRepairVerb(target, ref args);

    private void AddRepairVerb(EntityUid target, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !CanRequestMaintenance(target, args.User))
            return;

        var requester = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-engineering-request-repair"),
            Act = () =>
            {
                if (!CanRequestMaintenance(target, requester))
                    return;

                QueueMaintenance(target, requester, urgent: false);
            },
        });
    }

    private bool CanRequestMaintenance(EntityUid target, EntityUid requester) =>
        _configuration.GetCVar(CCVars.LivingStationEnabled) && _mobState.IsAlive(requester) &&
        _blocker.CanInteract(requester, target) && _interaction.InRangeAndAccessible(requester, target) &&
        _engineers.Any(worker => CanWorkMaintenance(worker) && IsMaintenanceTarget(target, worker));

    private bool CanWorkMaintenance(EntityUid worker) => _mobState.IsAlive(worker) && !HasComp<ActorComponent>(worker);

    private bool IsMaintenanceTarget(EntityUid target, EntityUid worker) =>
        !TerminatingOrDeleted(target) && !Paused(target) &&
        Transform(worker).GridUid is { } grid && Transform(target).ParentUid == grid &&
        !HasComp<MobStateComponent>(target) &&
        (NeedsBulb(target) || HasComp<RepairableComponent>(target) && _damage.GetTotalDamage(target) > 0 ||
         NeedsInfrastructureWork(target));

    private bool NeedsBulb(EntityUid target) => TryComp(target, out PoweredLightComponent? light) &&
        (!TryComp(_lights.GetBulb(target, light), out LightBulbComponent? bulb) || bulb.State != LightBulbState.Normal);

    private void DispatchMaintenance()
    {
        if (_timing.CurTime < _nextMaintenance || !_configuration.GetCVar(CCVars.LivingStationEnabled) || _engineers.Count == 0)
            return;

        _nextMaintenance = _timing.CurTime + TimeSpan.FromSeconds(5);
        foreach (var (requester, request) in _recentRepairs)
        {
            if (TerminatingOrDeleted(requester) || request.Expires <= _timing.CurTime)
                _recentRepairs.Remove(requester);
        }

        foreach (var worker in _engineers)
        {
            if (CanWorkMaintenance(worker) && !HasComp<LivingStationWorkClaimComponent>(worker))
                DispatchMaintenance(worker);
        }
    }

    private void DispatchMaintenance(EntityUid worker)
    {
        for (var i = 0; i < _maintenanceRequests.Count; i++)
        {
            var (target, requester) = _maintenanceRequests[i];
            if (!IsMaintenanceTarget(target, worker))
            {
                if (_engineers.Any(other => CanWorkMaintenance(other) && IsMaintenanceTarget(target, other)))
                    continue;

                NotifyRequester(requester, MaintenanceResult(target, "living-station-engineering-work-unavailable"));
                UpdateRepairMemory(requester, target, "living-station-engineering-work-unavailable");
                _maintenanceRequests.RemoveAt(i--);
                continue;
            }

            if (!TryAssignMaintenance(worker, target, requester))
                continue;

            _maintenanceRequests.RemoveAt(i);
            return;
        }

        foreach (var entry in _retryMaintenance.ToArray())
        {
            if (entry.Value <= _timing.CurTime || TerminatingOrDeleted(entry.Key))
                _retryMaintenance.Remove(entry.Key);
        }
        var position = Transform(worker).Coordinates;
        var services = EntityQueryEnumerator<EngineeringServicePointComponent>();
        while (services.MoveNext(out var uid, out _))
        {
            if (!_retryMaintenance.ContainsKey(uid) && IsMaintenanceTarget(uid, worker) && TryAssignMaintenance(worker, uid))
                return;
        }

        var nearby = _lookup.GetEntitiesInRange<PoweredLightComponent>(position, 8f, LookupFlags.Uncontained)
            .Select(light => light.Owner)
            .Concat(_lookup.GetEntitiesInRange<RepairableComponent>(position, 8f, LookupFlags.Uncontained)
                .Where(device => HasComp<Content.Shared.Atmos.Components.AtmosDeviceComponent>(device.Owner))
                .Select(device => device.Owner))
            .Where(uid => uid != Fixture && !_retryMaintenance.ContainsKey(uid) && IsMaintenanceTarget(uid, worker))
            .OrderBy(uid => (Transform(uid).LocalPosition - position.Position).LengthSquared());
        foreach (var uid in nearby)
        {
            if (TryAssignMaintenance(worker, uid))
                return;
        }
    }

    private bool TryAssignMaintenance(EntityUid worker, EntityUid target, EntityUid? requester = null)
    {
        if (!_work.TryAssign(worker, new EntityCoordinates(target, Vector2.Zero), MaintenanceTimeout, requester))
            return false;

        AddComp<EngineeringRepairComponent>(worker).Requester = requester;
        if (requester is { } player)
            UpdateRepairMemory(player, target, "living-station-engineering-work-unavailable", worker);
        EnsureComp<ActiveNPCComponent>(worker);
        _retryMaintenance[target] = _timing.CurTime + TimeSpan.FromMinutes(1);
        return true;
    }

    private void UpdateMaintenance(EntityUid worker, EngineeringRepairComponent order, EntityUid target,
        TransformComponent transform, LivingStationWorkClaimComponent claim)
    {
        if (!order.CableInstalled && !IsMaintenanceTarget(target, worker))
        {
            CompleteWork(worker, target, "living-station-engineering-work-complete");
            return;
        }

        if (_timing.CurTime >= claim.Deadline)
        {
            CompleteWork(worker, target, "living-station-engineering-work-timeout");
            return;
        }

        if (_doAfter.IsRunning(order.Welding))
            return;

        if (UpdateInfrastructure(worker, target, transform))
            return;

        if (NeedsBulb(target) && TryComp(target, out PoweredLightComponent? light) && !HasReplacement(worker, light.BulbType))
        {
            order.Stage = "living-station-engineering-stage-stock";
            if (!TryRefillLightReplacer(worker, light.BulbType))
                CompleteWork(worker, target, "living-station-engineering-work-supplies");
            return;
        }

        if (!NeedsBulb(target) && TryComp(target, out RepairableComponent? fuelTarget) &&
            FindHeldTool<WelderComponent>(worker) is { } fuelTool && !EnsureWelderFuel(worker, fuelTool, fuelTarget.FuelCost, target))
            return;

        if (!_interaction.InRangeAndAccessible(worker, target))
        {
            order.Stage = "living-station-engineering-stage-travel";
            if (NeedsBulb(target))
            {
                if (!ApproachLight(worker, target, transform, order))
                    CompleteWork(worker, target, "living-station-engineering-work-access");
                return;
            }

            var approach = TryComp(target, out EngineeringRepairFixtureComponent? fixture)
                ? transform.Coordinates.Offset(fixture.ApproachOffset)
                : transform.Coordinates;
            if (!ApproachMaintenance(worker, approach, fixture != null ? ApproachRange : 1.25f))
                CompleteWork(worker, target, "living-station-engineering-work-access");
            return;
        }

        _steering.Unregister(worker);
        if (NeedsBulb(target) && FindHeldTool<LightReplacerComponent>(worker) is { } replacer)
        {
            _interaction.InteractUsing(worker, replacer, target, transform.Coordinates);
            if (NeedsBulb(target))
                CompleteWork(worker, target, "living-station-engineering-work-tools");
            return;
        }

        if (!TryComp(target, out RepairableComponent? repairable) || FindHeldTool<WelderComponent>(worker) is not { } welder)
        {
            CompleteWork(worker, target, "living-station-engineering-work-tools");
            return;
        }

        if (!_blocker.CanInteract(worker, target) || !_blocker.CanUseHeldEntity(worker, welder))
        {
            CompleteWork(worker, target, "living-station-engineering-work-tools");
            return;
        }

        order.Welder = welder;
        order.Stage = "living-station-engineering-stage-repair";
        if (!_toggle.IsActivated(welder))
            _toggle.TryActivate(welder, worker, predicted: false);
        if (!_repairable.TryStartRepair((target, repairable), worker, welder, out order.Welding) || order.Welding == null)
            CompleteWork(worker, target, "living-station-engineering-work-tools");
    }

    private bool ApproachLight(EntityUid worker, EntityUid target, TransformComponent fixture, EngineeringRepairComponent order)
    {
        if (!_configuration.GetCVar(CCVars.PathfindingEnabled) || !_configuration.GetCVar(CCVars.NPCEnabled) ||
            fixture.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var map))
            return false;

        var origin = Transform(worker).Coordinates;
        if (_pathfinding.GetPoly(origin) == null)
            return true;

        if (order.LightApproaches != null && TryComp<NPCSteeringComponent>(worker, out var steering))
        {
            if (steering.Status == SteeringStatus.Moving)
                return true;

            _steering.Unregister(worker, steering);
        }

        if (order.LightApproaches == null)
        {
            var tile = _map.TileIndicesFor(grid, map, fixture.Coordinates);
            var candidates = new List<EntityCoordinates>();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                var candidate = _map.GridTileToLocal(grid, map, tile + new Vector2i(x, y));
                if (!_map.GetTileRef(grid, map, candidate).Tile.IsEmpty && _pathfinding.GetPoly(candidate) is { } poly &&
                    poly.IsValid() && _interaction.InRangeUnobstructed(_transforms.ToMapCoordinates(candidate),
                        target, predicate: obstacle => obstacle == worker))
                    candidates.Add(candidate);
            }

            order.LightApproaches = new Queue<EntityCoordinates>(candidates.OrderBy(candidate =>
                origin.TryDistance(EntityManager, candidate, out var distance) ? distance : float.MaxValue));
        }

        if (!order.LightApproaches.TryDequeue(out var approach))
            return false;

        _steering.Unregister(worker);
        var movement = _steering.Register(worker, approach);
        movement.Range = ApproachRange;
        movement.RepathRange = ApproachRange;
        return true;
    }

    private bool ApproachMaintenance(EntityUid worker, EntityCoordinates destination, float range)
    {
        if (TryComp(worker, out NPCSteeringComponent? steering))
        {
            if (steering.Coordinates.Equals(destination))
                return steering.Status == SteeringStatus.Moving;

            _steering.Unregister(worker, steering);
        }

        if (_pathfinding.GetPoly(Transform(worker).Coordinates) != null)
        {
            var movement = _steering.Register(worker, destination);
            movement.Range = range;
            movement.RepathRange = range;
        }

        return true;
    }

    private EntityUid? FindHeldTool<T>(EntityUid worker) where T : Component
    {
        foreach (var item in _hands.EnumerateHeld(worker))
        {
            if (HasComp<T>(item))
                return item;
        }

        return null;
    }

    private bool TryRefillLightReplacer(EntityUid worker, LightBulbType type)
    {
        if (FindHeldTool<LightReplacerComponent>(worker) is not { } replacer ||
            !TryComp(replacer, out EntityProviderComponent? provider))
            return false;

        var tool = Comp<LightReplacerComponent>(replacer);
        var replacement = type == LightBulbType.Tube ? tool.ActiveLightTube : tool.ActiveLightBulb;
        var stockrooms = EntityQueryEnumerator<CargoStockroomComponent, EntityStorageComponent, TransformComponent>();
        while (stockrooms.MoveNext(out var uid, out var stockroom, out var storage, out var transform))
        {
            if (stockroom.Department != CargoDepartment.Engineering || storage.Open || transform.GridUid != Transform(worker).GridUid)
                continue;

            foreach (var bulb in storage.Contents.ContainedEntities.ToArray())
            {
                if (MetaData(bulb).EntityPrototype?.ID != replacement.Id ||
                    !TryComp(bulb, out LightBulbComponent? light) || light.State != LightBulbState.Normal)
                    continue;

                if (!_interaction.InRangeAndAccessible(worker, uid))
                    return ApproachMaintenance(worker, transform.Coordinates, 1.25f);

                _steering.Unregister(worker);
                return _provider.TryInsertIntoProvider((replacer, provider), bulb, worker);
            }

            _cargo.RequestEngineeringRestock(uid);
        }

        return false;
    }

    private void StopMaintenance(EntityUid worker, EngineeringRepairComponent order)
    {
        if (_doAfter.IsRunning(order.Welding))
            _doAfter.Cancel(order.Welding);
        if (order.Welder is { } welder && Exists(welder))
            _toggle.TryDeactivate(welder, worker, predicted: false);
        PutAwayMaintenanceSupplies(worker, order);
    }
}
