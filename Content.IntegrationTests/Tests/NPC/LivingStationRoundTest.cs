using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Fluids.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.LivingStation;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
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
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

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
            () => SComp<MedicalChemistryKitComponent>(chemistryKit).Delivered &&
                  SComp<LivingStationMedicComponent>(medic.Owner).ChemistryKit == null, maxTicks: 3300, tickStep: 10);

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
            foreach (var item in cargoLookup.GetEntitiesInRange<Content.Shared.Item.ItemComponent>(
                         SComp<TransformComponent>(chemistryKit).Coordinates, 0.7f, LookupFlags.Uncontained))
                transforms.SetCoordinates(item, medic.Comp.TreatmentPoint.Offset(-Vector2.UnitX));
            Assert.That(Server.System<SharedEntityStorageSystem>().TryCloseStorage(chemistryKit), Is.True);
        });

        await PoolManager.WaitUntil(Server, () =>
            transforms.GetGridTilePositionOrDefault(chemistryKit) == transforms.GetGridTilePositionOrDefault(returnsPallet) &&
            SComp<PullableComponent>(chemistryKit).Puller == null, maxTicks: 3300, tickStep: 10);

        await Server.WaitRunTicks(330);
        await Server.WaitAssertion(() =>
        {
            var medicData = SComp<LivingStationMedicComponent>(medic.Owner);
            Assert.That(medicData.ChemistryKit, Is.Null, "Both medicines are above their reorder points");
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
            Assert.That(refillBeakers.Count(beaker =>
                solutions.TryGetSolution(beaker, "beaker", out _, out var mixture) && mixture.Volume == 0),
                Is.EqualTo(StartingMedications.Count - 1));

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
