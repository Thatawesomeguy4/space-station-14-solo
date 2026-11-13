using System.Linq;
using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class MedicalChemistryKitComponent : Component
{
    public bool Delivered;
}

[RegisterComponent]
public sealed partial class MedicalChemistryWorkComponent : Component;

public sealed partial class LivingStationMedicalChemistrySystem : EntitySystem
{
    private static readonly EntProtoId Kit = "LivingStationMedicalChemistryKit";
    // Add ingredients in reaction order. Each recipe starts in a clean beaker.
    private static readonly MedicationBatch[] Formulary =
    [
        new("Kelotane", 20, [("Silicon", 10), ("Carbon", 10)]),
        new("Inaprovaline", 30, [("Oxygen", 10), ("Carbon", 10), ("Sugar", 10)]),
        new("Dylovene", 30, [("Silicon", 10), ("Nitrogen", 10), ("Potassium", 10)]),
        new("Bicaridine", 24, [("Oxygen", 4), ("Carbon", 4), ("Sugar", 4), ("Carbon", 12)]),
        new("Dermaline", 30, [("Silicon", 5), ("Carbon", 5), ("Oxygen", 10), ("Phosphorus", 10)]),
        new("Cryptobiolin", 30, [("Potassium", 10), ("Oxygen", 10), ("Sugar", 10)]),
        new("Ethylredoxrazine", 27, [("Silicon", 3), ("Nitrogen", 3), ("Potassium", 3), ("Oxygen", 9), ("Carbon", 9)]),
        new("Hyronalin", 24, [("Silicon", 4), ("Nitrogen", 4), ("Potassium", 4), ("Radium", 12)]),
        new("Arithrazine", 24, [("Silicon", 2), ("Nitrogen", 2), ("Potassium", 2), ("Radium", 6), ("Hydrogen", 12)]),
        new("Tricordrazine", 24, [("Oxygen", 4), ("Carbon", 4), ("Sugar", 4), ("Silicon", 4), ("Nitrogen", 4), ("Potassium", 4)]),
        new("TranexamicAcid", 27, [("Sulfur", 3), ("Oxygen", 6), ("Hydrogen", 3), ("Carbon", 3), ("Sugar", 3), ("Oxygen", 3), ("Sugar", 9)]),
        new("Heparin", 24, [("Sulfur", 4), ("Oxygen", 8), ("Hydrogen", 4), ("Nitrogen", 12), ("Sodium", 12)]),
        new("PotassiumIodide", 20, [("Potassium", 10), ("Iodine", 10)]),
        new("Mannitol", 30, [("Hydrogen", 10), ("Water", 10), ("Sugar", 10)]),
        new("Synaptizine", 30, [("Lithium", 10), ("Sugar", 10), ("Water", 10)]),
        new("Lipozine", 30, [("TableSalt", 10), ("Ethanol", 10), ("Radium", 10)]),
        new("Saline", 25, [("Water", 20), ("TableSalt", 5)]),
        new("Bruizine", 24, [("Oxygen", 2), ("Carbon", 2), ("Sugar", 2), ("Carbon", 6), ("Lithium", 10.8f), ("Sugar", 12)]),
    ];

    private sealed record MedicationBatch(
        ProtoId<ReagentPrototype> Product,
        int Volume,
        (ProtoId<ReagentPrototype> Reagent, FixedPoint2 Amount)[] Ingredients);

    private static readonly TimeSpan WorkTimeout = TimeSpan.FromSeconds(20);
    private const float MixRange = 1.25f;
    private const float MedbayRadius = 4f;

    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LivingStationCargoSystem _cargo = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedEntityStorageSystem _storage = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SolutionTransferSystem _transfer = default!;

    private TimeSpan _nextCheck;

    public override void Initialize()
    {
        SubscribeLocalEvent<MedicalChemistryWorkComponent, ComponentShutdown>(OnWorkShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    private bool Enabled => _ticker.RunLevel == GameRunLevel.InRound && _config.GetCVar(CCVars.LivingStationEnabled);

    internal void RequestChemicals(Entity<LivingStationMedicComponent> medic, EntityUid requester)
    {
        if (!Enabled || medic.Comp.TreatmentPoint == null || !CanWork(medic) || !_mobs.IsAlive(requester) ||
            !_blocker.CanInteract(requester, medic) || !_interaction.InRangeAndAccessible(requester, medic.Owner))
            return;

        if (HasPendingKit(medic.Comp))
        {
            _popup.PopupEntity(Loc.GetString("living-station-medical-chemicals-pending"), medic, requester);
            return;
        }

        if (GetLowStock(medic).Count == 0)
        {
            _popup.PopupEntity(Loc.GetString("living-station-medical-chemicals-stocked"), medic, requester);
            return;
        }

        var queued = _cargo.RequestSupplies(medic, Kit);
        _popup.PopupEntity(Loc.GetString(queued
            ? "living-station-medical-chemicals-requested"
            : "living-station-medical-chemicals-unavailable"), medic, requester);
    }

    public override void Update(float frameTime)
    {
        if (!Enabled)
            return;

        if (_timing.CurTime >= _nextCheck)
        {
            _nextCheck = _timing.CurTime + TimeSpan.FromSeconds(5);
            var medics = EntityQueryEnumerator<LivingStationMedicComponent>();
            while (medics.MoveNext(out var worker, out var medic))
            {
                if (_timing.CurTime < medic.NextChemistryAttempt || !CanWork(worker) ||
                    medic.TreatmentPoint is not { } point || Transform(worker).GridUid != point.EntityId)
                    continue;

                if (!HasPendingKit(medic))
                {
                    if (GetLowStock((worker, medic)).Count > 0)
                        _cargo.RequestSupplies(worker, Kit);
                    continue;
                }

                if (TryGetKitInMedbay((worker, medic), out var crate) && GetLowStock((worker, medic)).Count > 0 &&
                    _work.TryAssign(worker, new EntityCoordinates(crate, Vector2.Zero), WorkTimeout))
                {
                    AddComp<MedicalChemistryWorkComponent>(worker);
                    EnsureComp<ActiveNPCComponent>(worker);
                }
            }
        }

        var orders = EntityQueryEnumerator<MedicalChemistryWorkComponent, LivingStationMedicComponent>();
        while (orders.MoveNext(out var worker, out _, out var medic))
        {
            if (!TryComp(worker, out LivingStationWorkClaimComponent? claim) || !CanWork(worker) ||
                !TryGetKitInMedbay((worker, medic), out var crate) || claim.Target.EntityId != crate ||
                _timing.CurTime >= claim.Deadline)
            {
                RemComp<MedicalChemistryWorkComponent>(worker);
                continue;
            }

            if (_interaction.InRangeAndAccessible(worker, crate, MixRange))
            {
                var needs = GetLowStock((worker, medic));
                if (needs.Count > 0)
                {
                    // Any unused reagents and partial mixtures stay in their containers.
                    medic.ChemistryKit = null;
                    var mixed = TryMix(worker, crate, needs);
                    _popup.PopupEntity(Loc.GetString(mixed
                        ? "living-station-medical-chemicals-mixed"
                        : "living-station-medical-chemicals-missing"), worker);
                }
                RemComp<MedicalChemistryWorkComponent>(worker);
                continue;
            }

            if (TryComp(worker, out NPCSteeringComponent? steering))
            {
                if (steering.Status is SteeringStatus.NoPath or SteeringStatus.InRange)
                    RemComp<MedicalChemistryWorkComponent>(worker);
            }
            else if (_pathfinding.GetPoly(Transform(worker).Coordinates) != null && _pathfinding.GetPoly(claim.Target) != null)
                _steering.Register(worker, claim.Target).Range = MixRange;
        }
    }

    private bool CanWork(EntityUid worker) => _mobs.IsAlive(worker) && !HasComp<ActorComponent>(worker);

    private bool HasPendingKit(LivingStationMedicComponent medic) =>
        medic.ChemistryKit is { } crate && !TerminatingOrDeleted(crate) && !EntityManager.IsQueuedForDeletion(crate);

    private List<MedicationBatch> GetLowStock(Entity<LivingStationMedicComponent> medic)
    {
        var point = medic.Comp.TreatmentPoint!.Value;
        var stock = new Dictionary<ProtoId<ReagentPrototype>, FixedPoint2>();
        medic.Comp.MedicationBeakers.RemoveAll(beaker => TerminatingOrDeleted(beaker) || EntityManager.IsQueuedForDeletion(beaker));
        foreach (var beaker in medic.Comp.MedicationBeakers)
        {
            var transform = Transform(beaker);
            if (transform.GridUid != point.EntityId || _containers.IsEntityOrParentInContainer(beaker) ||
                !point.TryDistance(EntityManager, transform.Coordinates, out var distance) || distance > MedbayRadius ||
                !_solutions.TryGetSolution(beaker, "beaker", out _, out var solution) ||
                solution.Contents.Count != 1)
                continue;

            var reagent = solution.Contents[0].Reagent.Prototype;
            stock[reagent] = stock.GetValueOrDefault(reagent) + solution.Volume;
        }

        return Formulary.Where(batch => stock.GetValueOrDefault(batch.Product) * 2 < batch.Volume).ToList();
    }

    private bool TryGetKitInMedbay(Entity<LivingStationMedicComponent> medic, out EntityUid crate)
    {
        crate = medic.Comp.ChemistryKit.GetValueOrDefault();
        return medic.Comp.TreatmentPoint is { } point && TryComp(crate, out MedicalChemistryKitComponent? kit) &&
               kit.Delivered && !EntityManager.IsQueuedForDeletion(crate) && !_containers.IsEntityInContainer(crate) &&
               TryComp(crate, out PullableComponent? pullable) && pullable.Puller == null &&
               Transform(medic).GridUid is { } grid && Transform(crate).GridUid == grid &&
               point.TryDistance(EntityManager, Transform(crate).Coordinates, out var distance) && distance <= MedbayRadius;
    }

    private bool TryMix(EntityUid worker, EntityUid crate, List<MedicationBatch> needs)
    {
        if (!TryComp(crate, out EntityStorageComponent? storage) || storage.Open ||
            !_blocker.CanInteract(worker, crate))
            return false;

        var contents = storage.Contents.ContainedEntities.ToArray();
        var beakers = contents
            .Where(item => _solutions.TryGetSolution(item, "beaker", out _, out var solution) &&
                           solution.Volume == 0)
            .Take(needs.Count)
            .ToArray();
        if (beakers.Length == 0 || !_storage.TryOpenStorage(worker, crate))
            return false;

        var made = 0;
        for (var i = 0; i < beakers.Length; i++)
        {
            if (!Mix(worker, beakers[i], needs[i], contents))
                continue;

            Comp<LivingStationMedicComponent>(worker).MedicationBeakers.Add(beakers[i]);
            made++;
        }

        return made == needs.Count;
    }

    private EntityUid? FindIngredient(EntityUid[] contents, ProtoId<ReagentPrototype> reagent, FixedPoint2 amount)
    {
        foreach (var item in contents)
        {
            if (_solutions.TryGetDrainableSolution(item, out _, out var solution) &&
                solution.Volume >= amount && solution.GetTotalPrototypeQuantity(reagent) == solution.Volume)
                return item;
        }

        return null;
    }

    private bool Mix(EntityUid worker, EntityUid beaker, MedicationBatch batch, EntityUid[] contents)
    {
        if (!_interaction.InRangeAndAccessible(worker, beaker) ||
            !_solutions.TryGetRefillableSolution(beaker, out var target, out var mixture))
            return false;

        var sources = new Dictionary<ProtoId<ReagentPrototype>, EntityUid>();
        foreach (var group in batch.Ingredients.GroupBy(ingredient => ingredient.Reagent))
        {
            var amount = group.Aggregate(FixedPoint2.Zero, (sum, ingredient) => sum + ingredient.Amount);
            if (FindIngredient(contents, group.Key, amount) is not { } ingredient ||
                !_interaction.InRangeAndAccessible(worker, ingredient))
                return false;

            sources.Add(group.Key, ingredient);
        }

        foreach (var (reagent, amount) in batch.Ingredients)
        {
            var ingredient = sources[reagent];
            if (!_solutions.TryGetDrainableSolution(ingredient, out var source, out _) ||
                _transfer.Transfer(new SolutionTransferData(worker, ingredient, source.Value, beaker, target.Value,
                    amount)) != amount)
                return false;
        }

        return mixture.Volume == batch.Volume &&
               mixture.GetTotalPrototypeQuantity(batch.Product) == mixture.Volume;
    }

    private void OnWorkShutdown(EntityUid worker, MedicalChemistryWorkComponent order, ComponentShutdown args)
    {
        if (TryComp(worker, out LivingStationMedicComponent? medic))
            medic.NextChemistryAttempt = _timing.CurTime + TimeSpan.FromSeconds(5);

        _steering.Unregister(worker);
        RemComp<ActiveNPCComponent>(worker);
        _work.Release(worker);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        _nextCheck = TimeSpan.Zero;
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var orders = AllEntityQuery<MedicalChemistryWorkComponent>();
        while (orders.MoveNext(out var worker, out _))
            RemComp<MedicalChemistryWorkComponent>(worker);
    }
}
