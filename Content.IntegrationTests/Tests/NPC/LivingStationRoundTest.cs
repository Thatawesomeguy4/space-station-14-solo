using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Cargo.Systems;
using Content.Server.Power.Components;
using Content.Shared.APC;
using Content.Shared.Power.Components;
using Robust.Shared.Map.Components;
using Content.Server.Fluids.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.LivingStation;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.Power.EntitySystems;
using Content.Shared.Cargo.Components;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Fluids.Components;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Stacks;
using Content.Shared.Station.Systems;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.IntegrationTests.Tests.NPC;

[TestFixture]
public sealed class LivingStationRoundTest : GameTest
{
    private static readonly ProtoId<DamageTypePrototype> Heat = "Heat";
    private static readonly ProtoId<ReagentPrototype> Kelotane = "Kelotane";
    private static readonly Dictionary<string, int> StartingMedications = new()
    {
        ["Kelotane"] = 20,
        ["Inaprovaline"] = 30,
        ["Dylovene"] = 30,
        ["Bicaridine"] = 24,
        ["Dermaline"] = 30,
        ["Cryptobiolin"] = 30,
        ["Ethylredoxrazine"] = 27,
        ["Hyronalin"] = 24,
        ["Arithrazine"] = 24,
        ["Tricordrazine"] = 24,
        ["TranexamicAcid"] = 27,
        ["Heparin"] = 24,
        ["PotassiumIodide"] = 20,
        ["Mannitol"] = 30,
        ["Synaptizine"] = 30,
        ["Lipozine"] = 30,
        ["Saline"] = 25,
        ["Bruizine"] = 24,
    };

    public override PoolSettings PoolSettings => new()
    {
        Dirty = true,
        DummyTicker = false,
        Map = PoolManager.TestStation,
    };

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task WorkBoardCapturesHandsOffBaselineWithoutChangingOrders()
    {
        var ticker = Server.System<ServerGameTicker>();
        var diagnostics = Server.System<LivingStationDiagnosticsSystem>();
        var work = Server.System<LivingStationWorkSystem>();
        EntityUid cabinet = default;
        var baseline = new List<string>();
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, false);
            Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
            for (var i = 0; i < LivingStationStaffing.Departments.Count; i++)
                Server.CfgMan.SetCVar(LivingStationStaffing.Departments[i], i == 0 ? 3 : 1);
            ticker.RestartRound();
            Assert.That(Server.System<LivingStationSystem>().Crew, Has.Count.EqualTo(8));
            var assigned = work.Assigned;
            Assert.That(assigned, Is.GreaterThanOrEqualTo(2), "Startup Cargo and Medical assignments must be counted.");
            var first = diagnostics.ReadReport();
            Assert.That(diagnostics.ReadReport(), Is.EqualTo(first), "Reading the work board must be side-effect free.");
            Assert.That(work.Assigned, Is.EqualTo(assigned));
            Assert.That(first.Count(line => line.Contains("[autonomous]")), Is.EqualTo(8));
            baseline.Add("BASELINE START\n" + string.Join('\n', first));

            var stockrooms = SEntMan.EntityQueryEnumerator<CargoStockroomComponent, EntityStorageComponent>();
            while (stockrooms.MoveNext(out var uid, out var room, out var storage))
            {
                baseline.Add($"INITIAL STOCK {room.Department}: " + string.Join(", ",
                    storage.Contents.ContainedEntities.Select(item => SComp<MetaDataComponent>(item).EntityPrototype?.ID)));
                if (room.Department == CargoDepartment.Engineering)
                    cabinet = uid;
            }
            Assert.That(cabinet.IsValid(), Is.True);
        });
        // Write on the test's execution context; pooled server threads may belong to an earlier test.
        TestContext.Out.WriteLine(string.Join('\n', baseline));

        // The simulated minute advances normal systems without issuing any work orders.
        await Pair.RunTicksSync(1800);
        await Server.WaitAssertion(() =>
        {
            Assert.That(work.Completed, Is.GreaterThanOrEqualTo(1), "At least one native starter order should complete unaided.");
            baseline.Clear();
            baseline.Add("BASELINE END\n" + string.Join('\n', diagnostics.ReadReport()));
            var random = Server.ResolveDependency<IRobustRandom>();
            var samples = new HashSet<int>();
            for (var i = 0; i < 32; i++)
                samples.Add(random.Next());
            Assert.That(samples.Count, Is.GreaterThan(1), "Parallel NPC routing must not corrupt the station RNG into repeated identical draws.");
            baseline.Add("Station RNG after autonomous routing: sample variation verified.");
            var stockroom = SComp<EntityStorageComponent>(cabinet);
            foreach (var item in stockroom.Contents.ContainedEntities.ToArray())
                SEntMan.DeleteEntity(item);
            var station = Server.System<StationSystem>().GetOwningStation(cabinet)!.Value;
            Server.System<CargoSystem>().TrySetBankAccount(station, "Cargo", 0);
            Assert.That(Server.System<LivingStationCargoSystem>().RequestSupplies(cabinet, "LivingStationEngineeringRestock"), Is.True);
            var report = string.Join('\n', diagnostics.ReadReport());
            Assert.That(report, Does.Contain("insufficient cargo funds"));
            ticker.RestartRound();
            Assert.That(work.Completed, Is.Zero);
            Assert.That(string.Join('\n', diagnostics.ReadReport()), Does.Not.Contain("insufficient cargo funds"));
        });
        TestContext.Out.WriteLine(string.Join('\n', baseline));
    }

    [Test]
    public async Task WorkCountersDistinguishReservationsOutcomesAndRelease()
    {
        var work = Server.System<LivingStationWorkSystem>();
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            Server.System<ServerGameTicker>().RestartRound();
            var origin = SComp<TransformComponent>(GetBarCounter().Owner).Coordinates;
            var first = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician", origin);
            var second = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician", origin);
            var assigned = work.Assigned;
            var released = work.Released;
            var busy = work.RejectedBusy;
            var reserved = work.RejectedReserved;
            var failed = work.Failed;
            Assert.That(work.TryAssign(first, origin, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(work.TryAssign(first, origin.Offset(Vector2.UnitX), TimeSpan.FromSeconds(10)), Is.False);
            Assert.That(work.TryAssign(second, origin, TimeSpan.FromSeconds(10)), Is.False);
            Assert.That(work.Assigned, Is.EqualTo(assigned + 1));
            Assert.That(work.RejectedBusy, Is.EqualTo(busy + 1));
            Assert.That(work.RejectedReserved, Is.EqualTo(reserved + 1));
            work.RecordOutcome(first, "Test route unavailable", success: false);
            work.Release(first);
            work.Release(first);
            Assert.That(work.Failed, Is.EqualTo(failed + 1));
            Assert.That(work.Released, Is.EqualTo(released + 1));
            Assert.That(SComp<LivingStationWorkHistoryComponent>(first).LastFailure, Is.EqualTo("Test route unavailable"));
            Assert.That(work.TryAssign(second, origin, TimeSpan.FromSeconds(10)), Is.True);
            SEntMan.DeleteEntity(second);
            Assert.That(work.Released, Is.EqualTo(released + 2), "Deleting a worker also releases its claim.");
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task DailyRoutineVisitsBarAndYieldsToWork()
    {
        var ticker = Server.System<ServerGameTicker>();
        var idle = Server.System<LivingStationIdleSystem>();
        var work = Server.System<LivingStationWorkSystem>();
        EntityUid visitor = default;
        EntityCoordinates home = default;
        LivingStationIdleRouteComponent route = default!;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            var bar = GetBarCounter();
            home = SComp<TransformComponent>(bar.Owner).Coordinates.Offset(-bar.Comp.StaffOffset * 2);
            visitor = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician", home);
            idle.Register(visitor, LivingStationDepartment.Science);
            route = SComp<LivingStationIdleRouteComponent>(visitor);
        });

        await Pair.RunTicksSync(30);
        await Server.WaitPost(() =>
        {
            route.NextWalk = TimeSpan.Zero;
            route.NextBreak = TimeSpan.Zero;
        });
        for (var i = 0; i < 280 && (route.Activity != LivingStationRoutine.BarBreak || route.Walking); i++)
            await Pair.RunTicksSync(5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(route.Activity, Is.EqualTo(LivingStationRoutine.BarBreak),
                $"Walking={route.Walking}, Home={route.Home}, Position={SComp<TransformComponent>(visitor).Coordinates}, Destination={route.Destination}");
            Assert.That(route.Walking, Is.False);
            Assert.That(route.ReturnHome, Is.True);
            Assert.That(work.TryAssign(visitor, home, TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(route.Activity, Is.EqualTo(LivingStationRoutine.ReturningHome));
            Assert.That(route.Walking, Is.False);
        });
        await Pair.RunTicksSync(10);
        await Server.WaitAssertion(() =>
        {
            Assert.That(route.Walking, Is.False);
            work.Release(visitor);
            route.NextWalk = TimeSpan.Zero;
        });
        await PoolManager.WaitUntil(Server, () => !route.ReturnHome && !route.Walking,
            maxTicks: 1400, tickStep: 5);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<TransformComponent>(visitor).Coordinates.TryDistance(SEntMan, home, out var distance), Is.True);
            Assert.That(distance, Is.LessThan(0.7f));
            ticker.RestartRound();
            Assert.That(SEntMan.EntityExists(visitor), Is.False);
        });
    }

    [Test]
    public async Task StaffingCountsApplyAtRoundStart()
    {
        var ticker = Server.System<ServerGameTicker>();
        await Server.WaitAssertion(() =>
        {
            var settings = LivingStationStaffing.Departments;
            var previous = settings.Select(setting => Server.CfgMan.GetCVar(setting)).ToArray();
            try
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                foreach (var setting in settings)
                    Server.CfgMan.SetCVar(setting, 2);
                ticker.RestartRound();
                Assert.That(Server.CfgMan.GetCVar(CCVars.LivingStationStaffingStatus), Is.EqualTo("2,2,2,2,2,1|2,2,2,2,2,1|2,2,2,2,2,2"));
                Assert.That(GetJanitors(), Has.Count.EqualTo(1));
                Assert.That(GetBarCounter().Comp.Bartender, Is.Not.Null);
                var pickups = SEntMan.EntityQueryEnumerator<CargoDeliveryPickupComponent>();
                Assert.That(pickups.MoveNext(out var pickup), Is.True);
                Assert.That(pickup.Workers, Has.Count.EqualTo(2));
                Assert.That(SEntMan.Count<LivingStationEngineerComponent>(), Is.EqualTo(2));
                Assert.That(SEntMan.Count<LivingStationMedicComponent>(), Is.EqualTo(2));
                Assert.That(SEntMan.Count<SecurityPatrolComponent>(), Is.EqualTo(2));

                foreach (var setting in settings)
                    Server.CfgMan.SetCVar(setting, 0);
                Assert.That(GetJanitors(), Has.Count.EqualTo(1));
                Assert.That(Server.CfgMan.GetCVar(CCVars.LivingStationStaffingStatus), Is.EqualTo("2,2,2,2,2,1|2,2,2,2,2,1|2,2,2,2,2,2"));
                ticker.RestartRound();
                Assert.That(Server.CfgMan.GetCVar(CCVars.LivingStationStaffingStatus), Is.EqualTo("0,0,0,0,0,0|0,0,0,0,0,0|0,0,0,0,0,0"));
                Assert.That(GetJanitors(), Is.Empty);
                Assert.That(GetBarCounter().Comp.Bartender, Is.Null);
                Assert.That(SEntMan.Count<LivingStationEngineerComponent>(), Is.Zero);
                Assert.That(SEntMan.Count<LivingStationMedicComponent>(), Is.Zero);
                Assert.That(SEntMan.Count<SecurityPatrolComponent>(), Is.Zero);
            }
            finally
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, false);
                for (var i = 0; i < settings.Count; i++)
                    Server.CfgMan.SetCVar(settings[i], previous[i]);
            }
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task EngineeringReplacesMissingLocalCable()
    {
        var ticker = Server.System<ServerGameTicker>();
        var transform = Server.System<SharedTransformSystem>();
        var map = Server.System<SharedMapSystem>();
        var power = Server.System<PowerReceiverSystem>();
        EntityUid engineer = default;
        EntityUid apc = default;
        EntityUid lamp = default;
        EntityCoordinates gap = default;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            (engineer, _) = GetRepair();
            var points = SEntMan.EntityQueryEnumerator<EngineeringServicePointComponent, ApcComponent, TransformComponent>();
            while (points.MoveNext(out var uid, out var service, out _, out var position))
            {
                if (service.CableOffset is not { } offset)
                    continue;
                apc = uid;
                gap = position.Coordinates.Offset(offset);
                break;
            }
            Assert.That(apc.IsValid(), Is.True);
            lamp = SEntMan.SpawnAttachedTo("PoweredSmallLight", gap);
            Server.System<ExtensionCableSystem>().SetReceiverReceptionRange(lamp, 0);
        });

        await PoolManager.WaitUntil(Server, () => SComp<ApcComponent>(apc).LastExternalState != ApcExternalPowerState.None &&
            power.IsPowered(lamp) && !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), maxTicks: 1200, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            var originalCable = FindCable();
            Assert.That(originalCable.IsValid(), Is.True);
            SEntMan.DeleteEntity(originalCable);
            transform.SetCoordinates(engineer, gap.Offset(Vector2.UnitY));
            var reporter = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(apc).Coordinates);
            ExecuteServiceVerb(apc, reporter, "Prioritize Engineering response");
            transform.SetCoordinates(reporter, gap.Offset(Vector2.UnitY));
            ExecuteServiceVerb(engineer, reporter, "Check Engineering work orders");
        });

        await PoolManager.WaitUntil(Server, () => !power.IsPowered(lamp), maxTicks: 200);
        await PoolManager.WaitUntil(Server, () => FindCable().IsValid() && power.IsPowered(lamp) &&
            !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), maxTicks: 2000, tickStep: 10);

        EntityUid FindCable()
        {
            var grid = gap.EntityId;
            var mapGrid = SComp<MapGridComponent>(grid);
            return map.GetAnchoredEntities((grid, mapGrid), map.TileIndicesFor(grid, mapGrid, gap))
                .FirstOrDefault(uid => SEntMan.TryGetComponent<CableComponent>(uid, out var cable) &&
                                       cable.CableType == Content.Shared.Power.CableType.Apc);
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task EngineeringPatchesMappedHullBreach()
    {
        var ticker = Server.System<ServerGameTicker>();
        var map = Server.System<SharedMapSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var atmos = Server.System<Content.Server.Atmos.EntitySystems.AtmosphereSystem>();
        EntityUid engineer = default;
        EntityUid alarm = default;
        EntityCoordinates hole = default;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            (engineer, _) = GetRepair();
            var points = SEntMan.EntityQueryEnumerator<EngineeringServicePointComponent, TransformComponent>();
            while (points.MoveNext(out var uid, out var service, out var position))
            {
                if (service.HullOffset is not { } offset)
                    continue;
                alarm = uid;
                hole = position.Coordinates.Offset(offset);
                break;
            }
            Assert.That(alarm.IsValid(), Is.True);
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer),
            maxTicks: 1000, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            var grid = hole.EntityId;
            map.SetTile(grid, SComp<MapGridComponent>(grid), hole, Tile.Empty);
            var service = SComp<EngineeringServicePointComponent>(alarm);
            transform.SetCoordinates(engineer, SComp<TransformComponent>(alarm).Coordinates.Offset(service.ApproachOffset));
            atmos.GetTileMixture(alarm, excite: true)?.Clear();
        });

        await PoolManager.WaitUntil(Server, () => !map.GetTileRef(hole.EntityId, SComp<MapGridComponent>(hole.EntityId), hole).Tile.IsEmpty,
            maxTicks: 2000, tickStep: 10);
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task EngineeringRepairsRequestedWallLightFromNearbyFloor()
    {
        var ticker = Server.System<ServerGameTicker>();
        var engineering = Server.System<LivingStationEngineeringSystem>();
        var lights = Server.System<SharedPoweredLightSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        EntityUid lamp = default;
        EntityUid requester = default;
        EntityUid worker = default;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            var engineer = engineering.ChiefEngineer!.Value;
            worker = engineer;
            var origin = SComp<TransformComponent>(engineer).Coordinates;
            var wall = origin.Offset(new Vector2(-3, -1));
            SEntMan.SpawnAttachedTo("WallReinforced", wall);
            lamp = SEntMan.SpawnAttachedTo("PoweredSmallLightEmpty", wall);
            SEntMan.EnsureComponent<Content.Shared.Wall.WallMountComponent>(lamp).Arc = new(Math.Tau);
            var reporter = SEntMan.SpawnAttachedTo("MobHuman", wall.Offset(Vector2.UnitY));
            requester = reporter;
            Assert.That(interaction.InRangeAndAccessible(engineer, lamp), Is.False);
            Assert.That(interaction.InRangeAndAccessible(reporter, lamp), Is.True);
            ExecuteServiceVerb(lamp, reporter, "Request Engineering repair");
            Assert.That(engineering.GetRecentRepairStatus(reporter), Does.Contain("still in the queue"));
            Assert.That(engineering.GetRecentRepairStatus(engineer), Is.Null);
        });

        await PoolManager.WaitUntil(Server, () =>
            SEntMan.TryGetComponent<LivingStationWorkClaimComponent>(worker, out var claim) && claim.Target.EntityId == lamp);
        await Server.WaitAssertion(() =>
            Assert.That(engineering.GetRecentRepairStatus(requester), Does.Contain("travelling to the repair point")));

        await PoolManager.WaitUntil(Server, () => lights.GetBulb(lamp) != null, maxTicks: 1400, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(worker));
        await Server.WaitAssertion(() =>
        {
            Assert.That(engineering.GetRecentRepairStatus(requester), Does.Contain("no further repair needed"));
            ticker.RestartRound();
            Assert.That(engineering.GetRecentRepairStatus(requester), Is.Null);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task EngineeringWeldsReportedDamageAndRestocksForLighting()
    {
        var ticker = Server.System<ServerGameTicker>();
        var damage = Server.System<DamageableSystem>();
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        var lights = Server.System<SharedPoweredLightSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var provider = Server.System<EntityProviderSystem>();
        EntityUid engineer = default;
        EntityUid window = default;
        EntityUid light = default;
        EntityUid replacement = default;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            (engineer, _) = GetRepair();
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer),
            maxTicks: 1000, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            var origin = SComp<TransformComponent>(engineer).Coordinates;
            window = SEntMan.SpawnAttachedTo("ReinforcedWindow", origin.Offset(new Vector2(-2, -1)));
            var reporter = SEntMan.SpawnAttachedTo("MobHuman", origin.Offset(new Vector2(-2, -2)));
            damage.TryChangeDamage(window, new DamageSpecifier(prototypes.Index(Heat), 20), ignoreResistances: true);
            Assert.That(damage.GetTotalDamage(window).Float(), Is.GreaterThan(0));
            ExecuteServiceVerb(window, reporter, "Request Engineering repair");
        });

        await PoolManager.WaitUntil(Server, () => damage.GetTotalDamage(window).Float() == 0 &&
            !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), maxTicks: 1600, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            var replacer = hands.EnumerateHeld(engineer).Single(item => SEntMan.HasComponent<LightReplacerComponent>(item));
            Assert.That(provider.TryGetEntities(replacer, "LightBulb", out var spares), Is.True);
            foreach (var bulb in spares!)
                SEntMan.DeleteEntity(bulb);

            var rooms = SEntMan.EntityQueryEnumerator<CargoStockroomComponent, EntityStorageComponent>();
            while (rooms.MoveNext(out var room, out var stockroom, out var storage))
            {
                if (stockroom.Department != CargoDepartment.Engineering)
                    continue;

                replacement = storage.Contents.ContainedEntities.First(item => SEntMan.HasComponent<LightBulbComponent>(item) &&
                    SComp<MetaDataComponent>(item).EntityPrototype?.ID == "LightBulb");
                var location = SComp<TransformComponent>(room).Coordinates.Offset(-Vector2.UnitY);
                light = SEntMan.SpawnAttachedTo("PoweredSmallLightEmpty", location);
                break;
            }

            Assert.That(light.IsValid(), Is.True);
        });

        await PoolManager.WaitUntil(Server, () => lights.GetBulb(light) == replacement &&
            !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), maxTicks: 1600, tickStep: 10);
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CargoProcessesOreAndDeliversRefinedMaterials()
    {
        var ticker = Server.System<ServerGameTicker>();
        var transform = Server.System<SharedTransformSystem>();
        var storage = Server.System<SharedEntityStorageSystem>();
        var cargo = Server.System<CargoSystem>();
        var lookup = Server.System<EntityLookupSystem>();
        var power = Server.System<PowerReceiverSystem>();
        var director = Server.System<LivingStationStoryDirectorSystem>();
        EntityUid processor = default;
        EntityUid bartender = default;
        var pallets = new Dictionary<CargoDepartment, EntityUid>();
        EntityUid console = default;
        EntityUid customer = default;
        EntityUid station = default;
        EntityUid crate = default;
        EntityUid keepsake = default;

        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            SEntMan.RemoveComponent<LivingStationMedicComponent>(GetTreatment().Medic.Owner);
            var palletQuery = SEntMan.EntityQueryEnumerator<CargoDepartmentPalletComponent>();
            while (palletQuery.MoveNext(out var uid, out var pallet))
                pallets.Add(pallet.Department, uid);

            var consoles = SEntMan.EntityQueryEnumerator<CargoOrderConsoleComponent, TransformComponent>();
            while (consoles.MoveNext(out var uid, out _, out var position))
            {
                if (position.LocalPosition == new Vector2(16.5f, 9.5f))
                    console = uid;
            }
            Assert.That(console.IsValid(), Is.True);
            station = Server.System<StationSystem>().GetOwningStation(console)!.Value;
            Assert.That(cargo.TrySetBankAccount(station, "Cargo", 0), Is.True);
            customer = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician",
                SComp<TransformComponent>(console).Coordinates.Offset(-Vector2.UnitX));
        });

        await PoolManager.WaitUntil(Server, () => power.IsPowered(console));
        await Server.WaitAssertion(() =>
        {
            ExecuteServiceVerb(console, customer, "Request 10 refined steel for Engineering");
            ExecuteServiceVerb(console, customer, "Request 10 refined glass for Science");
            var pickups = SEntMan.EntityQueryEnumerator<CargoDeliveryPickupComponent>();
            Assert.That(pickups.MoveNext(out var pickup), Is.True);
            Assert.That(pickup.Requisitions.Count(order => order.Requester == customer), Is.EqualTo(2));
            var processors = SEntMan.EntityQueryEnumerator<CargoOreProcessorComponent>();
            Assert.That(processors.MoveNext(out processor, out _), Is.True);
            bartender = GetBarCounter().Comp.Bartender!.Value;
        });

        await PoolManager.WaitUntil(Server, () => power.IsPowered(processor) &&
            !SEntMan.HasComponent<EngineeringRepairComponent>(Server.System<LivingStationEngineeringSystem>().ChiefEngineer!.Value),
            maxTicks: 1200, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            Assert.That(director.TryStartCargoPowerFault(Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>().CurTime), Is.True);
            var intake = SComp<TransformComponent>(pallets[CargoDepartment.OreIntake]).Coordinates;
            crate = SEntMan.SpawnAttachedTo("CrateGenericSteel", intake);
            foreach (var prototype in new[] { "SteelOre", "SteelOre", "Coal", "SpaceQuartz" })
                Assert.That(storage.Insert(SEntMan.SpawnAttachedTo(prototype, intake), crate), Is.True);
            keepsake = SEntMan.SpawnAttachedTo("SheetSteel1", intake);
            Assert.That(storage.Insert(keepsake, crate), Is.True);
        });
        await PoolManager.WaitUntil(Server, () => !power.IsPowered(processor), maxTicks: 150);
        await Server.WaitAssertion(() => Assert.That(power.IsPowered(console), Is.True));
        await PoolManager.WaitUntil(Server, () => power.IsPowered(processor) && !director.CargoFaultActive,
            maxTicks: 2200, tickStep: 10);

        await PoolManager.WaitUntil(Server, () => SComp<EntityStorageComponent>(crate).Contents.ContainedEntities.Count == 1,
            maxTicks: 2000, tickStep: 10);

        await PoolManager.WaitUntil(Server, () => HasRefinedDelivery(CargoDepartment.Engineering, "Steel") &&
            HasRefinedDelivery(CargoDepartment.Science, "Glass"), maxTicks: 5500, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<EntityStorageComponent>(crate).Contents.ContainedEntities, Is.EqualTo(new[] { keepsake }));
            Assert.That(cargo.GetBalanceFromAccount(station, "Cargo"), Is.Zero);
            ExecuteServiceVerb(console, customer, "Export surplus refined steel");
            Assert.That(cargo.GetBalanceFromAccount(station, "Cargo"), Is.EqualTo(400));
        });

        await PoolManager.WaitUntil(Server, () =>
        {
            var route = SComp<LivingStationIdleRouteComponent>(bartender);
            return route.Walking && SComp<TransformComponent>(bartender).Coordinates.TryDistance(SEntMan, route.Home, out var distance) &&
                   distance > 0.65f;
        }, maxTicks: 3600, tickStep: 5);

        bool HasRefinedDelivery(CargoDepartment department, string stackType)
        {
            var pallet = pallets[department];
            var position = SComp<TransformComponent>(pallet).Coordinates;
            var tile = transform.GetGridTilePositionOrDefault(pallet);
            return lookup.GetEntitiesInRange<EntityStorageComponent>(position, 0.71f, LookupFlags.Uncontained)
                .Any(delivery => transform.GetGridTilePositionOrDefault(delivery.Owner) == tile &&
                                 SComp<PullableComponent>(delivery.Owner).Puller == null &&
                                 delivery.Comp.Contents.ContainedEntities
                                     .Where(item => SEntMan.TryGetComponent<StackComponent>(item, out var stack) && stack.StackTypeId.Id == stackType)
                                     .Sum(item => SComp<StackComponent>(item).Count) == 10);
        }
    }

    [TestCase(CargoDepartment.Engineering, new[] { "CableApcStack10", "FloorTileItemSteel", "LightBulb", "LightBulb", "LightTube", "LightTube" })]
    [TestCase(CargoDepartment.Janitorial, new[] { "SprayBottleSpaceCleaner", "TrashBag" })]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CargoRestocksDepartmentsAndPaysForSteelExports(CargoDepartment department, string[] expectedStock)
    {
        var ticker = Server.System<ServerGameTicker>();
        var cargo = Server.System<CargoSystem>();
        var stationSystem = Server.System<StationSystem>();
        var power = Server.System<PowerReceiverSystem>();
        var storage = Server.System<SharedEntityStorageSystem>();
        var stacks = Server.System<SharedStackSystem>();
        EntityUid cabinet = default;
        EntityUid station = default;
        EntityUid console = default;
        EntityUid pickup = default;
        EntityUid manifestCrate = default;
        EntityUid exportStack = default;
        EntityUid customer = default;

        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            SEntMan.RemoveComponent<LivingStationMedicComponent>(GetTreatment().Medic.Owner);
            var stockrooms = SEntMan.EntityQueryEnumerator<CargoStockroomComponent>();
            while (stockrooms.MoveNext(out var uid, out var room))
            {
                if (room.Department == department)
                    cabinet = uid;
            }
            Assert.That(cabinet.IsValid(), Is.True);
            foreach (var item in SComp<EntityStorageComponent>(cabinet).Contents.ContainedEntities.ToArray())
                SEntMan.DeleteEntity(item);

            var consoles = SEntMan.EntityQueryEnumerator<CargoOrderConsoleComponent, TransformComponent>();
            while (consoles.MoveNext(out var uid, out _, out var transform))
            {
                if (transform.LocalPosition == new Vector2(16.5f, 9.5f))
                    console = uid;
            }
            Assert.That(console.IsValid(), Is.True);
            station = stationSystem.GetOwningStation(console)!.Value;
            Assert.That(cargo.TrySetBankAccount(station, "Cargo", 0), Is.True);
            var pickups = SEntMan.EntityQueryEnumerator<CargoDeliveryPickupComponent>();
            Assert.That(pickups.MoveNext(out pickup, out _), Is.True);
        });

        await PoolManager.WaitUntil(Server, () => power.IsPowered(console) &&
            SComp<CargoDeliveryPickupComponent>(pickup).Requisitions.Any(order => order.Requester == cabinet));

        await Server.WaitAssertion(() =>
        {
            customer = SEntMan.SpawnAttachedTo("MobLivingStationCargoTechnician",
                SComp<TransformComponent>(console).Coordinates.Offset(-Vector2.UnitX));
            ExecuteServiceVerb(console, customer, "Check cargo queue");
            ExecuteServiceVerb(console, customer, "Requisition glass sheets");
            Assert.That(SComp<CargoDeliveryPickupComponent>(pickup).Requisitions.Any(order => order.Requester == customer), Is.True);
            ExecuteServiceVerb(console, customer, "Cancel my queued requisitions");
            Assert.That(SComp<CargoDeliveryPickupComponent>(pickup).Requisitions.Any(order => order.Requester == customer), Is.False);

            var pallets = SEntMan.EntityQueryEnumerator<CargoDepartmentPalletComponent, TransformComponent>();
            while (pallets.MoveNext(out _, out var pallet, out var transform))
            {
                if (pallet.Department == CargoDepartment.Export)
                    manifestCrate = SEntMan.SpawnAttachedTo("CrateGenericSteel", transform.Coordinates);
            }
            Assert.That(manifestCrate.IsValid(), Is.True);
            var coordinates = SComp<TransformComponent>(manifestCrate).Coordinates;
            exportStack = SEntMan.SpawnAttachedTo("SheetSteel", coordinates);
            var wrongItem = SEntMan.SpawnAttachedTo("SheetGlass", coordinates);
            Assert.That(storage.Insert(exportStack, manifestCrate), Is.True);
            Assert.That(storage.Insert(wrongItem, manifestCrate), Is.True);
            ExecuteServiceVerb(console, customer, "Submit steel export contract");
            Assert.That(cargo.GetBalanceFromAccount(station, "Cargo"), Is.Zero);
            Assert.That(stacks.GetCount(exportStack), Is.EqualTo(30));

            SEntMan.DeleteEntity(wrongItem);
            ExecuteServiceVerb(console, customer, "Submit steel export contract");
            ExecuteServiceVerb(console, customer, "Submit steel export contract");
            Assert.That(cargo.GetBalanceFromAccount(station, "Cargo"), Is.EqualTo(400));
            Assert.That(stacks.GetCount(exportStack), Is.Zero);
        });

        await PoolManager.WaitUntil(Server, () => SComp<EntityStorageComponent>(cabinet).Contents.ContainedEntities.Count == expectedStock.Length,
            maxTicks: 3300, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            var stocked = SComp<EntityStorageComponent>(cabinet).Contents.ContainedEntities
                .Select(item => SComp<MetaDataComponent>(item).EntityPrototype?.ID);
            Assert.That(stocked, Is.EquivalentTo(expectedStock));
            Assert.That(cargo.GetBalanceFromAccount(station, "Cargo"), Is.EqualTo(150));
        });
    }

    [TestCase(CargoDepartment.Engineering, "Requisition electrical supplies", "CrateEngineeringElectricalSupplies")]
    [TestCase(CargoDepartment.Janitorial, "Requisition janitorial supplies", "CrateJanitorialSupplies")]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CargoDeliversDepartmentSupplies(CargoDepartment department, string request, string prototype)
    {
        var ticker = Server.System<ServerGameTicker>();
        var transform = Server.System<SharedTransformSystem>();
        var lookup = Server.System<EntityLookupSystem>();
        Entity<CargoDeliveryComponent> startupDelivery = default;
        EntityUid destination = default;
        EntityUid crate = default;
        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            startupDelivery = GetDelivery();
            var pickups = SEntMan.EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
            Assert.That(pickups.MoveNext(out var pickup, out _, out var pickupTransform), Is.True);
            var customer = SEntMan.SpawnAttachedTo("MobHuman", pickupTransform.Coordinates.Offset(Vector2.UnitY));
            ExecuteServiceVerb(pickup, customer, request);
            var pallets = SEntMan.EntityQueryEnumerator<CargoDepartmentPalletComponent>();
            while (pallets.MoveNext(out var uid, out var pallet))
            {
                if (pallet.Department == department)
                    destination = uid;
            }
            Assert.That(destination.IsValid(), Is.True);
            SEntMan.RemoveComponent<LivingStationMedicComponent>(GetTreatment().Medic.Owner);
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<CargoDeliveryComponent>(startupDelivery.Owner),
            maxTicks: 800, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            SEntMan.DeleteEntity(startupDelivery.Comp.Crate);
            var dropoff = SComp<TransformComponent>(startupDelivery.Comp.Dropoff).Coordinates;
            var obstruction = SEntMan.SpawnAttachedTo("CrateGenericSteel", dropoff);
            transform.AnchorEntity(obstruction);
            Assert.That(SComp<TransformComponent>(obstruction).Anchored, Is.True);
        });

        await PoolManager.WaitUntil(Server, () =>
        {
            var coordinates = SComp<TransformComponent>(destination).Coordinates;
            var tile = transform.GetGridTilePositionOrDefault(destination);
            crate = lookup.GetEntitiesInRange<EntityStorageComponent>(coordinates, 0.71f, LookupFlags.Uncontained)
                .Select(storage => storage.Owner)
                .FirstOrDefault(uid => SComp<MetaDataComponent>(uid).EntityPrototype?.ID == prototype &&
                                       transform.GetGridTilePositionOrDefault(uid) == tile &&
                                       SComp<PullableComponent>(uid).Puller == null);
            return crate.IsValid();
        }, maxTicks: 3300, tickStep: 10);

        await Server.WaitAssertion(() =>
            Assert.That(SComp<EntityStorageComponent>(crate).Contents.ContainedEntities, Is.Not.Empty));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task MedbayReceivesAndStocksChemicalShipments()
    {
        var ticker = Server.System<ServerGameTicker>();
        var solutions = Server.System<SharedSolutionContainerSystem>();
        var transforms = Server.System<SharedTransformSystem>();
        EntityUid medic = default;
        EntityUid shelf = default;
        EntityUid returns = default;
        EntityUid kit = default;
        EntityUid refill = default;

        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            var workers = SEntMan.EntityQueryEnumerator<LivingStationMedicComponent>();
            Assert.That(workers.MoveNext(out medic, out _), Is.True);
            var shelves = SEntMan.EntityQueryEnumerator<MedicalMedicationShelfComponent>();
            Assert.That(shelves.MoveNext(out shelf, out _), Is.True);
            var pallets = SEntMan.EntityQueryEnumerator<CargoDepartmentPalletComponent>();
            while (pallets.MoveNext(out var pallet, out var point))
            {
                if (point.Department == CargoDepartment.Returns)
                    returns = pallet;
            }
            Assert.That(returns.IsValid(), Is.True);
        });

        await PoolManager.WaitUntil(Server, () => (kit = SComp<LivingStationMedicComponent>(medic).ChemistryKit ?? kit).IsValid(),
            maxTicks: 1000, tickStep: 5);
        await PoolManager.WaitUntil(Server, () => SComp<MedicalChemistryKitComponent>(kit).CheckedIn,
            maxTicks: 3300, tickStep: 10);
        await PoolManager.WaitUntil(Server, () =>
            transforms.GetGridTilePositionOrDefault(kit) == transforms.GetGridTilePositionOrDefault(returns) &&
            SComp<PullableComponent>(kit).Puller == null, maxTicks: 3300, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<EntityStorageComponent>(kit).Contents.ContainedEntities, Is.Not.Empty);
            SEntMan.DeleteEntity(kit);
            var stock = SComp<Content.Shared.Storage.StorageComponent>(shelf).Container.ContainedEntities;
            Assert.That(stock.Select(BeakerContents), Is.EquivalentTo(StartingMedications.Select(batch => $"{batch.Key}:{batch.Value}")));
            var kelotane = stock.Single(item => BeakerContents(item) == "Kelotane:20");
            Assert.That(solutions.TryGetSolution(kelotane, "beaker", out var solution, out _), Is.True);
            solutions.SplitSolution(solution!.Value, 15);
        });

        await PoolManager.WaitUntil(Server, () => (refill = SComp<LivingStationMedicComponent>(medic).ChemistryKit ?? refill).IsValid(),
            maxTicks: 1000, tickStep: 5);
        await PoolManager.WaitUntil(Server, () => SComp<MedicalChemistryKitComponent>(refill).CheckedIn,
            maxTicks: 3300, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            var stock = SComp<Content.Shared.Storage.StorageComponent>(shelf).Container.ContainedEntities;
            Assert.That(stock.Select(BeakerContents), Is.EquivalentTo(StartingMedications.Select(batch => $"{batch.Key}:{batch.Value}")
                .Append("Kelotane:5")));
        });

        string BeakerContents(EntityUid item)
        {
            Assert.That(solutions.TryGetSolution(item, "beaker", out _, out var solution), Is.True);
            return string.Join("+", solution.Contents.Select(reagent => $"{reagent.Reagent.Prototype.Id}:{reagent.Quantity}"));
        }
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CrewWorksAndResetsOnSaltern()
    {
        var ticker = Server.System<ServerGameTicker>();
        var puddles = Server.System<PuddleSystem>();
        var solutions = Server.System<SharedSolutionContainerSystem>();
        var transforms = Server.System<SharedTransformSystem>();
        var pulling = Server.System<PullingSystem>();
        var security = Server.System<LivingStationSecuritySystem>();
        var lights = Server.System<SharedPoweredLightSystem>();
        var hands = Server.System<SharedHandsSystem>();
        var provider = Server.System<EntityProviderSystem>();
        var interaction = Server.System<SharedInteractionSystem>();
        var damage = Server.System<DamageableSystem>();
        var stacks = Server.System<SharedStackSystem>();
        var doAfter = Server.System<SharedDoAfterSystem>();
        var prototypes = Server.ResolveDependency<IPrototypeManager>();
        EntityUid previous = default;
        EntityUid spill = default;
        Entity<CargoDeliveryComponent> delivery = default;
        EntityUid manualPuller = default;
        Entity<SecurityPatrolComponent> officer = default;
        EntityUid reporter = default;
        EntityCoordinates reportedLocation = default;
        EntityUid engineer = default;
        EntityUid fixture = default;
        EntityUid engineerTool = default;
        Entity<MedicalTreatmentComponent> medic = default;
        EntityUid patient = default;
        EntityUid bruisePack = default;
        Entity<BarServiceCounterComponent> bar = default;
        EntityUid customer = default;
        EntityUid water = default;
        EntityUid manualWater = default;
        EntityUid secondCrate = default;
        EntityUid cargoPickup = default;
        EntityUid cargoCustomer = default;
        EntityUid orderedSteel = default;
        EntityUid cargoDropoff = default;
        EntityUid injuredCrew = default;
        EntityUid ointment = default;
        EntityUid chemistryKit = default;
        EntityUid refillKit = default;
        EntityUid medbayPallet = default;
        EntityUid returnsPallet = default;
        EntityUid[] beakers = [];
        EntityUid[] refillBeakers = [];

        await Server.WaitAssertion(() =>
        {
            Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
            Assert.That(Server.CfgMan.GetCVar(CCVars.LivingStationEnabled), Is.False);
            Assert.That(GetJanitors(), Is.Empty);
            Assert.That(SEntMan.EntityQueryEnumerator<CargoDeliveryComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<SecurityPatrolComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<EngineeringRepairComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<MedicalTreatmentComponent>().MoveNext(out _), Is.False);
            Assert.That(GetBarCounter().Comp.Bartender, Is.Null);

            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            ticker.RestartRound();
            previous = AssertOneJanitor(ticker);
            delivery = GetDelivery();
            officer = GetOfficer();
            bar = GetBarCounter();
            customer = SpawnBarCustomer(bar);
            RequestWater(bar, customer);
            var bartender = bar.Comp.Bartender!.Value;
            water = SComp<BarServiceOrderComponent>(bartender).Drink;
            RequestWater(bar, customer);
            Assert.That(SComp<BarServiceOrderComponent>(bartender).Drink, Is.EqualTo(water));
            (engineer, fixture) = GetRepair();
            (medic, patient) = GetTreatment();
            // Exercise local freight before the medic starts the chemistry workflow.
            SComp<LivingStationMedicComponent>(medic.Owner).NextChemistryAttempt = TimeSpan.MaxValue;
            bruisePack = hands.GetActiveItem(medic.Owner)!.Value;
            Assert.That(damage.GetTotalDamage(patient).Float(), Is.EqualTo(4));
            Assert.That(SComp<LightBulbComponent>(lights.GetBulb(fixture)!.Value).State, Is.EqualTo(LightBulbState.Burned));
            var grid = SComp<TransformComponent>(officer.Owner).GridUid!.Value;
            reportedLocation = new EntityCoordinates(grid, -7.5f, 12.5f);
            reporter = SEntMan.SpawnAttachedTo("MobHuman", reportedLocation);
            Assert.That(security.TryReport(reporter), Is.True);
            Assert.That(security.TryReport(reporter), Is.False);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(delivery.Owner), Is.True);
            var location = SComp<TransformComponent>(previous).Coordinates;
            Assert.That(puddles.TrySpillAt(location, new Solution("Blood", 10), out spill), Is.True);
        });

        await PoolManager.WaitUntil(Server,
            () => !SEntMan.TryGetComponent<PuddleComponent>(spill, out var puddle) ||
                  solutions.TryGetSolution(spill, puddle.SolutionName, out _, out var remaining) &&
                  puddles.CanFullyEvaporate(remaining),
            tickStep: 10);

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<CargoDeliveryComponent>(delivery.Owner),
            maxTicks: 800, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner), tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<BarServiceOrderComponent>(bar.Comp.Bartender!.Value), tickStep: 10);
        await PoolManager.WaitUntil(Server, () => officer.Comp.LastInspected == reportedLocation, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => officer.Comp.NextWaypoint == 2 &&
            transforms.GetGridTilePositionOrDefault(officer.Owner) ==
            transforms.GetGridTilePositionOrDefault(officer.Comp.Waypoints[1]));

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(officer.Owner), Is.False);
            Assert.That(transforms.GetGridTilePositionOrDefault(officer.Owner),
                Is.EqualTo(transforms.GetGridTilePositionOrDefault(officer.Comp.Waypoints[1])));

            var blockedLocation = new EntityCoordinates(reportedLocation.EntityId, -8.5f, 16.5f);
            var blockedReporter = SEntMan.SpawnAttachedTo("MobHuman", blockedLocation);
            Assert.That(security.TryReport(blockedReporter), Is.True);
            SEntMan.DeleteEntity(blockedReporter);
            SEntMan.SpawnAttachedTo("WallReinforced", blockedLocation);
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<LivingStationWorkClaimComponent>(officer.Owner),
            maxTicks: 800, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => SEntMan.HasComponent<NPCSteeringComponent>(officer.Owner));

        await Server.WaitAssertion(() =>
        {
            Assert.That(officer.Comp.LastInspected, Is.EqualTo(reportedLocation));
            Assert.That(transforms.GetGridTilePositionOrDefault(delivery.Comp.Crate),
                Is.EqualTo(transforms.GetGridTilePositionOrDefault(delivery.Comp.Dropoff)));
            Assert.That(SComp<PullableComponent>(delivery.Comp.Crate).Puller, Is.Null);
            Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.True);
            Assert.That(damage.GetAllDamage(patient).DamageDict.GetValueOrDefault("Blunt").Float(), Is.Zero);
            Assert.That(stacks.GetCount(bruisePack), Is.EqualTo(9));
            Assert.That(transforms.GetGridTilePositionOrDefault(water), Is.EqualTo(transforms.GetGridTilePositionOrDefault(bar.Owner)));
            Assert.That(hands.GetActiveItem(bar.Comp.Bartender!.Value), Is.Null);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(bar.Comp.Bartender!.Value), Is.False);

            var pickups = SEntMan.EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
            Assert.That(pickups.MoveNext(out cargoPickup, out var route, out var pickupTransform), Is.True);
            cargoCustomer = SEntMan.SpawnAttachedTo("MobHuman", pickupTransform.Coordinates.Offset(Vector2.UnitY));
            secondCrate = SEntMan.SpawnAttachedTo("CrateMaterialGlass", pickupTransform.Coordinates);
            var verbs = Server.System<SharedVerbSystem>();
            Assert.That(verbs.GetLocalVerbs(cargoPickup, cargoCustomer, typeof(Verb))
                .Any(verb => verb.Text == "Request crate delivery"), Is.False);
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Requisition steel sheets");
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Requisition steel sheets");
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Requisition janitorial supplies");
            Assert.That(route.Requisitions.Count(order => order.Requester == cargoCustomer), Is.EqualTo(2));
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Cancel my queued requisitions");
            Assert.That(route.Requisitions.Any(order => order.Requester == cargoCustomer), Is.False);
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Requisition steel sheets");
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Enable automatic crate hauling");
            cargoDropoff = delivery.Comp.Dropoff;
            SEntMan.DeleteEntity(delivery.Comp.Crate);

            var medicPosition = SComp<TransformComponent>(medic.Owner).Coordinates;
            injuredCrew = SEntMan.SpawnAttachedTo("MobHuman", medicPosition.Offset(Vector2.UnitX));
            damage.TryChangeDamage(injuredCrew, new DamageSpecifier(prototypes.Index(Heat), 4),
                ignoreResistances: true);
            ExecuteServiceVerb(medic.Owner, injuredCrew, "Request treatment");
            var followUp = GetTreatment();
            Assert.That(followUp.Medic.Owner, Is.EqualTo(medic.Owner));
            Assert.That(followUp.Patient, Is.EqualTo(injuredCrew));
            ointment = followUp.Medic.Comp.Medicine;
            Assert.That(ointment, Is.Not.EqualTo(bruisePack));
        });

        await PoolManager.WaitUntil(Server, () =>
            transforms.GetGridTilePositionOrDefault(secondCrate) == transforms.GetGridTilePositionOrDefault(cargoDropoff) &&
            !SEntMan.HasComponent<CargoDeliveryComponent>(delivery.Owner),
            maxTicks: 800, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner), tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(transforms.GetGridTilePositionOrDefault(secondCrate),
                Is.EqualTo(transforms.GetGridTilePositionOrDefault(cargoDropoff)),
                $"Worker at {SComp<TransformComponent>(delivery.Owner).Coordinates}");
            Assert.That(SComp<PullableComponent>(secondCrate).Puller, Is.Null);
            Assert.That(damage.GetAllDamage(injuredCrew).DamageDict.GetValueOrDefault("Heat").Float(), Is.Zero);
            Assert.That(stacks.GetCount(ointment), Is.EqualTo(9));
            Assert.That(stacks.GetCount(bruisePack), Is.EqualTo(9));

            var medicData = SComp<LivingStationMedicComponent>(medic.Owner);
            var customerPosition = SComp<TransformComponent>(medic.Owner).Coordinates.Offset(Vector2.UnitX);
            transforms.SetCoordinates(injuredCrew, customerPosition);
            medicData.NextChemistryAttempt = TimeSpan.Zero;
            ExecuteServiceVerb(medic.Owner, injuredCrew, "Request medication ingredients");
            SEntMan.DeleteEntity(secondCrate);
            var pallets = SEntMan.EntityQueryEnumerator<CargoDepartmentPalletComponent>();
            while (pallets.MoveNext(out var pallet, out var destination))
            {
                if (destination.Department == CargoDepartment.Medical)
                    medbayPallet = pallet;
                if (destination.Department == CargoDepartment.Returns)
                    returnsPallet = pallet;
            }
            Assert.That(medbayPallet.IsValid() && returnsPallet.IsValid(), Is.True);
        });

        await PoolManager.WaitUntil(Server, () => SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit != null,
            maxTicks: 3300, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            chemistryKit = SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit!.Value;
            beakers = SComp<EntityStorageComponent>(chemistryKit).Contents.ContainedEntities
                .Where(item => solutions.TryGetSolution(item, "beaker", out _, out _)).ToArray();
            Assert.That(beakers, Has.Length.EqualTo(StartingMedications.Count));
        });

        var cargoLookup = Server.System<EntityLookupSystem>();
        await PoolManager.WaitUntil(Server, () =>
        {
            var dropoff = SComp<TransformComponent>(cargoDropoff).Coordinates;
            var tile = transforms.GetGridTilePositionOrDefault(cargoDropoff);
            orderedSteel = cargoLookup.GetEntitiesInRange<EntityStorageComponent>(dropoff, 0.71f, LookupFlags.Uncontained)
                .Select(crate => crate.Owner)
                .FirstOrDefault(crate => transforms.GetGridTilePositionOrDefault(crate) == tile &&
                    SComp<MetaDataComponent>(crate).EntityPrototype?.ID == "CrateMaterialSteel");
            return orderedSteel.IsValid() && SComp<PullableComponent>(orderedSteel).Puller == null;
        }, maxTicks: 3300, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<EntityStorageComponent>(orderedSteel).Contents.ContainedEntities, Has.Count.EqualTo(3));
            SEntMan.DeleteEntity(orderedSteel);
            ExecuteServiceVerb(cargoPickup, cargoCustomer, "Disable automatic crate hauling");
            SEntMan.DeleteEntity(cargoCustomer);
        });

        await PoolManager.WaitUntil(Server,
            () =>
            {
                var medicState = SComp<LivingStationMedicComponent>(medic.Owner);
                if (medicState.ChemistryKit is { } current && current != chemistryKit &&
                    SEntMan.TryGetComponent<EntityStorageComponent>(current, out var storage))
                {
                    chemistryKit = current;
                    beakers = storage.Contents.ContainedEntities
                        .Where(item => solutions.TryGetSolution(item, "beaker", out _, out _)).ToArray();
                }

                return SEntMan.TryGetComponent<MedicalChemistryKitComponent>(chemistryKit, out var kit) && kit.Delivered &&
                       medicState.ChemistryKit == null;
            }, maxTicks: 6000, tickStep: 1);

        await Server.WaitAssertion(() =>
        {
            var mixtures = beakers.Select(beaker =>
            {
                Assert.That(solutions.TryGetSolution(beaker, "beaker", out _, out var solution), Is.True);
                return solution;
            }).ToArray();
            var actual = mixtures.Select(solution =>
                string.Join("+", solution.Contents.Select(reagent => $"{reagent.Reagent.Prototype.Id}:{reagent.Quantity}")));
            Assert.That(actual, Is.EquivalentTo(StartingMedications.Select(batch => $"{batch.Key}:{batch.Value}")));
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(medic.Owner), Is.False);
            Assert.That(transforms.GetGridTilePositionOrDefault(chemistryKit),
                Is.EqualTo(transforms.GetGridTilePositionOrDefault(medbayPallet)));
            var benches = SEntMan.EntityQueryEnumerator<MedicalMedicationShelfComponent>();
            Assert.That(benches.MoveNext(out var bench, out _), Is.True);
            Assert.That(SComp<Content.Shared.Storage.StorageComponent>(bench).Container.ContainedEntities,
                Is.SupersetOf(beakers));
            Assert.That(SComp<MedicalChemistryKitComponent>(chemistryKit).CheckedIn, Is.True);
            var returnedStorage = SComp<EntityStorageComponent>(chemistryKit);
            Assert.That(returnedStorage.Open, Is.False);
            Assert.That(returnedStorage.Contents.ContainedEntities, Is.Not.Empty);
        });

        await PoolManager.WaitUntil(Server, () =>
            transforms.GetGridTilePositionOrDefault(chemistryKit) == transforms.GetGridTilePositionOrDefault(returnsPallet) &&
            SComp<PullableComponent>(chemistryKit).Puller == null, maxTicks: 3300, tickStep: 10);

        await Server.WaitAssertion(() => SEntMan.DeleteEntity(chemistryKit));
        await Server.WaitRunTicks(330);
        await Server.WaitAssertion(() =>
        {
            var medicData = SComp<LivingStationMedicComponent>(medic.Owner);
            Assert.That(medicData.ChemistryKit, Is.Null, "All medications are above their reorder points");
            ExecuteServiceVerb(medic.Owner, injuredCrew, "Request medication ingredients");
            Assert.That(medicData.ChemistryKit, Is.Null);

            foreach (var beaker in beakers)
            {
                Assert.That(solutions.TryGetSolution(beaker, "beaker", out var solutionEntity, out var solution), Is.True);
                if (solution.GetTotalPrototypeQuantity(Kelotane) == 20)
                    solutions.SplitSolution(solutionEntity!.Value, 15);
            }
        });

        await PoolManager.WaitUntil(Server, () => SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit != null,
            maxTicks: 800, tickStep: 10);
        await Server.WaitAssertion(() =>
        {
            refillKit = SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit!.Value;
            Assert.That(refillKit, Is.Not.EqualTo(chemistryKit));
            refillBeakers = SComp<EntityStorageComponent>(refillKit).Contents.ContainedEntities
                .Where(item => solutions.TryGetSolution(item, "beaker", out _, out _)).ToArray();
            Assert.That(refillBeakers, Has.Length.EqualTo(StartingMedications.Count));
        });

        await PoolManager.WaitUntil(Server, () => SComp<MedicalChemistryKitComponent>(refillKit).Delivered &&
            SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit == null, maxTicks: 3300, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(refillBeakers.Count(beaker =>
                solutions.TryGetSolution(beaker, "beaker", out _, out var mixture) &&
                mixture.Volume == 20 && mixture.GetTotalPrototypeQuantity(Kelotane) == 20), Is.EqualTo(1));
            var refillContents = refillBeakers.Select(beaker =>
            {
                Assert.That(solutions.TryGetSolution(beaker, "beaker", out _, out var mixture), Is.True);
                return string.Join("+", mixture.Contents.Select(reagent => $"{reagent.Reagent.Prototype.Id}:{reagent.Quantity}"));
            }).Where(contents => contents.Length > 0);
            Assert.That(refillContents, Is.EqualTo(new[] { "Kelotane:20" }));

            Assert.That(security.TryReport(reporter), Is.True);
            ticker.RestartRound();
            Assert.That(SEntMan.EntityExists(previous), Is.False);
            Assert.That(SEntMan.EntityExists(delivery.Owner), Is.False);
            Assert.That(SEntMan.EntityExists(chemistryKit), Is.False);
            Assert.That(SEntMan.EntityExists(refillKit), Is.False);
            Assert.That(beakers.Any(SEntMan.EntityExists), Is.False);
            Assert.That(SEntMan.EntityExists(officer.Owner), Is.False);
            Assert.That(SEntMan.EntityExists(engineer), Is.False);
            Assert.That(SEntMan.EntityExists(medic.Owner), Is.False);
            Assert.That(SEntMan.EntityExists(patient), Is.False);
            Assert.That(SEntMan.EntityExists(bar.Comp.Bartender!.Value), Is.False);
            Assert.That(SEntMan.EntityExists(water), Is.False);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(GetOfficer().Owner), Is.False);
            previous = AssertOneJanitor(ticker);
            delivery = GetDelivery();
            (engineer, fixture) = GetRepair();
            engineerTool = hands.GetActiveItem(engineer)!.Value;
            var fixtureLocation = SComp<TransformComponent>(fixture).Coordinates;
            var approach = fixtureLocation.Offset(SComp<EngineeringRepairFixtureComponent>(fixture).ApproachOffset);
            var manualEngineer = SEntMan.SpawnAttachedTo("MobLivingStationEngineer", approach);
            interaction.UserInteraction(manualEngineer, fixtureLocation, fixture);
            Assert.That(SComp<LightBulbComponent>(lights.GetBulb(fixture)!.Value).State, Is.EqualTo(LightBulbState.Normal));

            (medic, patient) = GetTreatment();
            bruisePack = hands.GetActiveItem(medic.Owner)!.Value;
            var patientLocation = SComp<TransformComponent>(patient).Coordinates;
            var manualMedic = SEntMan.SpawnAttachedTo("MobLivingStationMedic", patientLocation.Offset(Vector2.UnitX));
            interaction.UserInteraction(manualMedic, patientLocation, patient);

            bar = GetBarCounter();
            customer = SpawnBarCustomer(bar);
            RequestWater(bar, customer);
            water = SComp<BarServiceOrderComponent>(bar.Comp.Bartender!.Value).Drink;
            manualWater = SEntMan.SpawnAttachedTo("DrinkWaterBottleFull", SComp<TransformComponent>(customer).Coordinates);
            Assert.That(hands.TryPickup(customer, manualWater), Is.True);
            var counterLocation = SComp<TransformComponent>(bar.Owner).Coordinates;
            Assert.That(hands.TryDrop(customer, manualWater, counterLocation.Offset(new Vector2(0.2f, 0))), Is.True);
            Assert.That(transforms.GetGridTilePositionOrDefault(manualWater), Is.EqualTo(transforms.GetGridTilePositionOrDefault(bar.Owner)));
        });

        await PoolManager.WaitUntil(Server, () => SComp<PullableComponent>(delivery.Comp.Crate).Puller == delivery.Owner);
        await Server.WaitAssertion(() =>
        {
            manualPuller = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(delivery.Comp.Crate).Coordinates);
            Assert.That(pulling.TryStartPull(manualPuller, delivery.Comp.Crate), Is.True);
        });
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<CargoDeliveryComponent>(delivery.Owner));
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer), tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner), tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<BarServiceOrderComponent>(bar.Comp.Bartender!.Value));

        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<PullableComponent>(delivery.Comp.Crate).Puller, Is.EqualTo(manualPuller));
            Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.True);
            Assert.That(provider.TryGetEntityCounter(engineerTool, out var stock), Is.True);
            Assert.That(stock!["LightTube"], Is.EqualTo(8));
            Assert.That(damage.GetAllDamage(patient).DamageDict.GetValueOrDefault("Blunt").Float(), Is.Zero);
            Assert.That(stacks.GetCount(bruisePack), Is.EqualTo(10));
            var counterLocation = SComp<TransformComponent>(bar.Owner).Coordinates;
            Assert.That(SComp<TransformComponent>(water).Coordinates.TryDistance(SEntMan,
                counterLocation.Offset(bar.Comp.StockOffset), out var distance), Is.True);
            Assert.That(distance, Is.LessThan(0.1f));
            Assert.That(transforms.GetGridTilePositionOrDefault(manualWater), Is.EqualTo(transforms.GetGridTilePositionOrDefault(bar.Owner)));
            ticker.RestartRound();
            Assert.That(SEntMan.EntityExists(previous), Is.False);
            Assert.That(SEntMan.EntityExists(delivery.Owner), Is.False);
            Assert.That(SEntMan.EntityExists(delivery.Comp.Crate), Is.False);
            AssertOneJanitor(ticker);
            delivery = GetDelivery();
            (engineer, fixture) = GetRepair();
            engineerTool = hands.GetActiveItem(engineer)!.Value;
            Assert.That(provider.TryEjectEntities(engineerTool, "LightTube", out _), Is.True);
            (medic, patient) = GetTreatment();
            bruisePack = hands.GetActiveItem(medic.Owner)!.Value;

            bar = GetBarCounter();
            customer = SpawnBarCustomer(bar);
            RequestWater(bar, customer);
            water = SComp<BarServiceOrderComponent>(bar.Comp.Bartender!.Value).Drink;
            SEntMan.DeleteEntity(water);

            var dropoff = SComp<TransformComponent>(delivery.Comp.Dropoff);
            SEntMan.SpawnAttachedTo("WallReinforced", dropoff.Coordinates);
        });

        await PoolManager.WaitUntil(Server, () => medic.Comp.Application != null);
        await Server.WaitPost(() => transforms.SetCoordinates(patient,
            medic.Comp.TreatmentPoint.Offset(new Vector2(5, 0))));
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner));
        await Server.WaitAssertion(() => Assert.That(doAfter.IsRunning(medic.Comp.Application), Is.False));
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<BarServiceOrderComponent>(bar.Comp.Bartender!.Value));

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<CargoDeliveryComponent>(delivery.Owner),
            maxTicks: 800, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer));

        await Server.WaitAssertion(() =>
        {
            Assert.That(transforms.GetGridTilePositionOrDefault(delivery.Comp.Crate),
                Is.Not.EqualTo(transforms.GetGridTilePositionOrDefault(delivery.Comp.Dropoff)));
            Assert.That(SComp<PullableComponent>(delivery.Comp.Crate).Puller, Is.Null);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineer), Is.False);
            Assert.That(SComp<LightBulbComponent>(lights.GetBulb(fixture)!.Value).State, Is.EqualTo(LightBulbState.Burned));
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(medic.Owner), Is.False);
            Assert.That(damage.GetAllDamage(patient).DamageDict.GetValueOrDefault("Blunt").Float(), Is.Positive);
            Assert.That(stacks.GetCount(bruisePack), Is.EqualTo(10));
            RequestWater(bar, customer);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(bar.Comp.Bartender!.Value), Is.False);

            ticker.RestartRound();
            (engineer, fixture) = GetRepair();
            var approach = SComp<TransformComponent>(fixture).Coordinates
                .Offset(SComp<EngineeringRepairFixtureComponent>(fixture).ApproachOffset);
            SEntMan.SpawnAttachedTo("WallReinforced", approach);
            (medic, patient) = GetTreatment();
            var treatmentSupply = hands.GetActiveItem(medic.Owner)!.Value;
            stacks.ReduceCount(treatmentSupply, stacks.GetCount(treatmentSupply));
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<EngineeringRepairComponent>(engineer),
            maxTicks: 800, tickStep: 10);
        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner));

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineer), Is.False);
            Assert.That(SComp<LightBulbComponent>(lights.GetBulb(fixture)!.Value).State, Is.EqualTo(LightBulbState.Burned));
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(medic.Owner), Is.False);
            Assert.That(damage.GetAllDamage(patient).DamageDict.GetValueOrDefault("Blunt").Float(), Is.Positive);

            ticker.RestartRound();
            (medic, patient) = GetTreatment();
            bruisePack = hands.GetActiveItem(medic.Owner)!.Value;
            var center = SComp<TransformComponent>(patient).Coordinates;
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        SEntMan.SpawnAttachedTo("WallReinforced", center.Offset(new Vector2(x, y)));
                }
            }
        });

        await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<MedicalTreatmentComponent>(medic.Owner),
            maxTicks: 800, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(medic.Owner), Is.False);
            Assert.That(damage.GetAllDamage(patient).DamageDict.GetValueOrDefault("Blunt").Float(), Is.Positive);
            Assert.That(stacks.GetCount(bruisePack), Is.EqualTo(10));
            bar = GetBarCounter();
            RequestWater(bar, SpawnBarCustomer(bar));
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(bar.Comp.Bartender!.Value), Is.True);
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, false);
            ticker.RestartRound();
            Assert.That(GetJanitors(), Is.Empty);
            Assert.That(SEntMan.EntityQueryEnumerator<CargoDeliveryComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<SecurityPatrolComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<EngineeringRepairComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<MedicalTreatmentComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityQueryEnumerator<LivingStationWorkClaimComponent>().MoveNext(out _), Is.False);
            Assert.That(SEntMan.EntityExists(bar.Comp.Bartender!.Value), Is.False);
            Assert.That(GetBarCounter().Comp.Bartender, Is.Null);
        });
    }

    private Entity<BarServiceCounterComponent> GetBarCounter()
    {
        var query = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent>();
        Assert.That(query.MoveNext(out var table, out var counter), Is.True);
        return (table, counter!);
    }

    private EntityUid SpawnBarCustomer(Entity<BarServiceCounterComponent> bar) =>
        SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(bar.Owner).Coordinates.Offset(-bar.Comp.StaffOffset));

    private void RequestWater(EntityUid counter, EntityUid customer) =>
        ExecuteServiceVerb(counter, customer, "Request water");

    private void ExecuteServiceVerb(EntityUid target, EntityUid requester, string text)
    {
        var verbs = Server.System<SharedVerbSystem>();
        var request = verbs.GetLocalVerbs(target, requester, typeof(Verb)).Single(verb => verb.Text == text);
        verbs.ExecuteVerb(request, requester, target);
    }

    private (Entity<MedicalTreatmentComponent> Medic, EntityUid Patient) GetTreatment()
    {
        var query = SEntMan.EntityQueryEnumerator<MedicalTreatmentComponent, LivingStationWorkClaimComponent>();
        Assert.That(query.MoveNext(out var worker, out var treatment, out var claim), Is.True);
        Assert.That(query.MoveNext(out _, out _), Is.False);
        return ((worker, treatment!), claim!.Target.EntityId);
    }

    private (EntityUid Engineer, EntityUid Fixture) GetRepair()
    {
        Assert.That(Server.System<LivingStationEngineeringSystem>().TryStartRepair(), Is.True);
        var query = SEntMan.EntityQueryEnumerator<EngineeringRepairComponent, LivingStationWorkClaimComponent>();
        Assert.That(query.MoveNext(out var worker, out _, out var claim), Is.True);
        Assert.That(query.MoveNext(out _, out _), Is.False);
        return (worker, claim!.Target.EntityId);
    }

    private Entity<SecurityPatrolComponent> GetOfficer()
    {
        var query = SEntMan.EntityQueryEnumerator<SecurityPatrolComponent>();
        Assert.That(query.MoveNext(out var officer, out var patrol), Is.True);
        Assert.That(query.MoveNext(out _), Is.False);
        return (officer, patrol!);
    }

    private Entity<CargoDeliveryComponent> GetDelivery()
    {
        var query = SEntMan.EntityQueryEnumerator<CargoDeliveryComponent>();
        Assert.That(query.MoveNext(out var worker, out var order), Is.True);
        Assert.That(query.MoveNext(out _), Is.False);
        return (worker, order!);
    }

    private List<EntityUid> GetJanitors()
    {
        var janitors = new List<EntityUid>();
        var query = SEntMan.EntityQueryEnumerator<HTNComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var metadata))
        {
            if (metadata.EntityPrototype?.ID == "MobLivingStationJanitor")
                janitors.Add(uid);
        }

        return janitors;
    }

    private EntityUid AssertOneJanitor(ServerGameTicker ticker)
    {
        Assert.That(ticker.RunLevel, Is.EqualTo(GameRunLevel.InRound));
        var janitors = GetJanitors();
        Assert.That(janitors, Has.Count.EqualTo(1));
        Assert.That(SComp<TransformComponent>(janitors[0]).MapID, Is.EqualTo(ticker.DefaultMap));
        return janitors[0];
    }
}
