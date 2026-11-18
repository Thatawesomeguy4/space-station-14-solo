using System.Linq;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
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
using Content.Shared.Storage;
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
    public bool CheckedIn;
    public EntityUid[]? UnpackedContents;
}

[RegisterComponent]
public sealed partial class MedicalChemistryWorkComponent : Component
{
    public TimeSpan? MixAfter;
}

[RegisterComponent]
public sealed partial class MedicalMedicationShelfComponent : Component;

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
    [Dependency] private ChatSystem _chat = default!;
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
    [Dependency] private SharedStorageSystem _shelves = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private SolutionTransferSystem _transfer = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private TimeSpan _nextCheck;
    private readonly HashSet<EntityUid> _medicationBeakers = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<MedicalChemistryWorkComponent, ComponentShutdown>(OnWorkShutdown);
        SubscribeLocalEvent<MedicalChemistryKitComponent, StorageBeforeOpenEvent>(OnOpenKit);
        SubscribeLocalEvent<MedicalChemistryKitComponent, StorageBeforeCloseEvent>(OnCloseKit);
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

                if (TryGetKitInMedbay((worker, medic), out var crate) &&
                    _work.TryAssign(worker, new EntityCoordinates(crate, Vector2.Zero), WorkTimeout))
                {
                    AddComp<MedicalChemistryWorkComponent>(worker);
                    EnsureComp<ActiveNPCComponent>(worker);
                }
            }
        }

        var orders = EntityQueryEnumerator<MedicalChemistryWorkComponent, LivingStationMedicComponent>();
        while (orders.MoveNext(out var worker, out var order, out var medic))
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
                _steering.Unregister(worker);
                ReceiveKit(worker, medic, crate, order);
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
        _medicationBeakers.RemoveWhere(beaker => TerminatingOrDeleted(beaker) || EntityManager.IsQueuedForDeletion(beaker));
        foreach (var beaker in _medicationBeakers)
        {
            var transform = Transform(beaker);
            if (transform.GridUid != point.EntityId ||
                (_containers.IsEntityOrParentInContainer(beaker) && !HasComp<MedicalMedicationShelfComponent>(transform.ParentUid)) ||
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

    private void ReceiveKit(EntityUid worker, LivingStationMedicComponent medic, EntityUid crate, MedicalChemistryWorkComponent order)
    {
        if (!TryComp(crate, out EntityStorageComponent? storage) || !_blocker.CanInteract(worker, crate))
            return;

        var kit = Comp<MedicalChemistryKitComponent>(crate);
        if (order.MixAfter == null)
        {
            if (!storage.Open)
            {
                if (!_storage.TryOpenStorage(worker, crate))
                {
                    RemComp<MedicalChemistryWorkComponent>(worker);
                    return;
                }
            }

            Speak(worker, "living-station-medical-chemicals-received");
            order.MixAfter = _timing.CurTime + TimeSpan.FromSeconds(2);
            return;
        }

        if (_timing.CurTime < order.MixAfter)
            return;

        var needs = GetLowStock((worker, medic));
        var stocked = needs.Count > 0 && storage.Open && TryMix(worker, crate, kit, medic.TreatmentPoint!.Value, needs);
        if (storage.Open && !_storage.TryCloseStorage(crate, worker))
        {
            RemComp<MedicalChemistryWorkComponent>(worker);
            return;
        }

        kit.CheckedIn = true;
        kit.UnpackedContents = null;
        medic.ChemistryKit = null;
        Speak(worker, stocked ? "living-station-medical-chemicals-mixed" : needs.Count == 0
            ? "living-station-medical-chemicals-shelf-stocked" : "living-station-medical-chemicals-missing");
        RemComp<MedicalChemistryWorkComponent>(worker);
    }

    private bool TryMix(EntityUid worker, EntityUid crate, MedicalChemistryKitComponent kit, EntityCoordinates treatment,
        List<MedicationBatch> needs)
    {
        var bench = EntityQueryEnumerator<MedicalMedicationShelfComponent, StorageComponent, TransformComponent>();
        EntityUid? shelf = null;
        var stock = Transform(worker).Coordinates;
        while (bench.MoveNext(out var surface, out _, out _, out var transform))
        {
            if (!treatment.TryDistance(EntityManager, transform.Coordinates, out var distance) || distance > MedbayRadius ||
                !_interaction.InRangeAndAccessible(worker, surface))
                continue;

            shelf = surface;
            break;
        }

        var contents = (kit.UnpackedContents ?? []).Where(item => !TerminatingOrDeleted(item) &&
            !_containers.IsEntityOrParentInContainer(item) &&
            Transform(item).Coordinates.TryDistance(EntityManager, Transform(crate).Coordinates, out var distance) && distance <= 0.7f)
            .ToArray();
        var beakers = contents
            .Where(item => _solutions.TryGetSolution(item, "beaker", out _, out var solution) &&
                           solution.Volume == 0)
            .Take(needs.Count)
            .ToArray();
        var made = 0;
        for (var i = 0; i < beakers.Length; i++)
        {
            if (!Mix(worker, beakers[i], needs[i], contents))
                continue;

            if (shelf is not { } surface || !_shelves.Insert(surface, beakers[i], out _, user: worker))
            {
                var offset = new Vector2((i % 5 - 2) * 0.13f, (i / 5 - 1) * 0.13f);
                _transform.SetCoordinates(beakers[i], stock.Offset(offset));
            }
            _medicationBeakers.Add(beakers[i]);
            made++;
        }

        return made == needs.Count;
    }

    private void OnOpenKit(Entity<MedicalChemistryKitComponent> kit, ref StorageBeforeOpenEvent args)
    {
        if (kit.Comp.Delivered && !kit.Comp.CheckedIn && TryComp<EntityStorageComponent>(kit, out var storage))
            kit.Comp.UnpackedContents = storage.Contents.ContainedEntities.ToArray();
    }

    private void OnCloseKit(Entity<MedicalChemistryKitComponent> kit, ref StorageBeforeCloseEvent args)
    {
        if (args.User is { } worker && HasComp<MedicalChemistryWorkComponent>(worker) &&
            TryComp<LivingStationMedicComponent>(worker, out var medic) && medic.ChemistryKit == kit.Owner)
            args.Contents.RemoveWhere(item => kit.Comp.UnpackedContents?.Contains(item) != true || _medicationBeakers.Contains(item));
    }

    private void Speak(EntityUid worker, string message) =>
        _chat.TrySendInGameICMessage(worker, Loc.GetString(message), InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false);

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

        _medicationBeakers.Clear();
        var orders = AllEntityQuery<MedicalChemistryWorkComponent>();
        while (orders.MoveNext(out var worker, out _))
            RemComp<MedicalChemistryWorkComponent>(worker);
    }
}
