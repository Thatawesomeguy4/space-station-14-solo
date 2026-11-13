using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Station.Systems;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationMedicalSystem : EntitySystem
{
    private static readonly EntProtoId Medic = "MobLivingStationMedic";
    private static readonly EntProtoId Patient = "MobLivingStationPatient";
    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";
    private static readonly ProtoId<TagPrototype> BruisePack = "Brutepack";
    private static readonly ProtoId<TagPrototype> Ointment = "Ointment";
    private static readonly TimeSpan TreatmentTimeout = TimeSpan.FromSeconds(20);
    private const float TreatmentRange = 1.25f;
    private const float TreatmentAreaRadius = 4f;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private LivingStationMedicalChemistrySystem _chemistry = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PathfindingSystem _pathfinding = default!;
    [Dependency] private SharedStackSystem _stacks = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private TagSystem _tags = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<LivingStationMedicComponent, GetVerbsEvent<Verb>>(OnGetTreatmentVerbs);
        SubscribeLocalEvent<MedicalTreatmentComponent, ComponentShutdown>(OnTreatmentShutdown);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void StartTreatment(MapId mapId)
    {
        var markers = EntityQueryEnumerator<MedicalTreatmentPointComponent, TransformComponent>();
        while (markers.MoveNext(out var marker, out var point, out var transform))
        {
            if (transform.MapID != mapId || _station.GetOwningStation(marker, transform) == null)
                continue;

            var patient = SpawnAttachedTo(Patient, transform.Coordinates);
            _damage.TryChangeDamage(patient, new DamageSpecifier(_prototypes.Index(Blunt), 4), ignoreResistances: true);
            var worker = SpawnAttachedTo(Medic, transform.Coordinates.Offset(point.WorkerOffset));
            var medic = Comp<LivingStationMedicComponent>(worker);
            medic.TreatmentPoint = transform.Coordinates;
            if (!TryAssignTreatment((worker, medic), patient))
            {
                QueueDel(patient);
                QueueDel(worker);
                return;
            }

            return;
        }
    }

    private void OnGetTreatmentVerbs(Entity<LivingStationMedicComponent> medic, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanInteract || !args.CanAccess || medic.Comp.TreatmentPoint == null || args.User == medic.Owner ||
            _ticker.RunLevel != GameRunLevel.InRound || !_config.GetCVar(CCVars.LivingStationEnabled))
            return;

        var patient = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-medical-request-chemicals"),
            Act = () => _chemistry.RequestChemicals(medic, patient),
        });
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("living-station-medical-request-treatment"),
            Act = () =>
            {
                if (_ticker.RunLevel != GameRunLevel.InRound || !_config.GetCVar(CCVars.LivingStationEnabled) ||
                    !_blocker.CanInteract(patient, medic) || !_interaction.InRangeAndAccessible(patient, medic.Owner))
                    return;

                var accepted = TryAssignTreatment(medic, patient);
                _popup.PopupEntity(Loc.GetString(accepted
                    ? "living-station-medical-treatment-accepted"
                    : "living-station-medical-treatment-unavailable"), medic, patient);
            },
        });
    }

    private bool TryAssignTreatment(Entity<LivingStationMedicComponent> medic, EntityUid patient)
    {
        if (medic.Comp.TreatmentPoint is not { } point || !_mobState.IsAlive(medic) || HasComp<ActorComponent>(medic) ||
            !_mobState.IsAlive(patient) || !TryComp(patient, out TransformComponent? patientTransform) ||
            Transform(medic).GridUid is not { } grid || patientTransform.GridUid != grid ||
            !point.TryDistance(EntityManager, patientTransform.Coordinates, out var distance) || distance > TreatmentAreaRadius)
            return false;

        if (FindMedicine(medic, patient) is not { } medicine ||
            !_work.TryAssign(medic, new EntityCoordinates(patient, Vector2.Zero), TreatmentTimeout))
            return false;

        var treatment = AddComp<MedicalTreatmentComponent>(medic);
        treatment.TreatmentPoint = point;
        treatment.Medicine = medicine;
        treatment.HealingProfile = medicine.Comp.Damage;
        EnsureComp<ActiveNPCComponent>(medic);
        return true;
    }

    public override void Update(float frameTime)
    {
        var treatments = EntityQueryEnumerator<MedicalTreatmentComponent, TransformComponent>();
        while (treatments.MoveNext(out var worker, out var treatment, out var transform))
        {
            if (!TryComp(worker, out LivingStationWorkClaimComponent? claim) ||
                !_mobState.IsAlive(worker) || HasComp<ActorComponent>(worker) ||
                !TryComp(claim.Target.EntityId, out TransformComponent? patientTransform) ||
                !_mobState.IsAlive(claim.Target.EntityId) || transform.GridUid == null ||
                transform.GridUid != patientTransform.GridUid ||
                !treatment.TreatmentPoint.TryDistance(EntityManager, patientTransform.Coordinates, out var patientDistance) ||
                patientDistance > TreatmentAreaRadius)
            {
                Finish(worker, "cancelled because the medic or patient is unavailable");
                continue;
            }

            var patient = claim.Target.EntityId;
            var damage = GetTreatableDamage(patient, treatment.HealingProfile);
            if (damage <= FixedPoint2.Zero)
            {
                Finish(worker, "completed, patient no longer has damage this medicine can treat");
                continue;
            }

            if (_timing.CurTime >= claim.Deadline)
            {
                Finish(worker, "timed out before the patient was treated");
                continue;
            }

            if (treatment.Application is { } application)
            {
                var status = _doAfter.GetStatus(application);
                if (status == DoAfterStatus.Finished && damage < treatment.DamageAtApplication)
                    Finish(worker, "completed, treatment was applied");
                else if (status != DoAfterStatus.Running)
                    Finish(worker, "ended without reducing the patient's injury");
                continue;
            }

            if (!_hands.IsHolding(worker, treatment.Medicine, out var hand) ||
                !HasComp<HealingComponent>(treatment.Medicine) || _stacks.GetCount(treatment.Medicine) <= 0)
            {
                Finish(worker, "cancelled because the selected medicine is unavailable");
                continue;
            }

            if (transform.Coordinates.TryDistance(EntityManager, patientTransform.Coordinates, out var distance) &&
                distance <= TreatmentRange && _interaction.InRangeAndAccessible(worker, patient))
            {
                _steering.Unregister(worker);
                _hands.TrySetActiveHand(worker, hand);
                if (_hands.GetActiveItem(worker) != treatment.Medicine)
                {
                    Finish(worker, "could not ready the selected medicine");
                    continue;
                }
                var doAfters = EnsureComp<DoAfterComponent>(worker);
                var nextId = doAfters.NextId;
                _interaction.UserInteraction(worker, patientTransform.Coordinates, patient);
                if (doAfters.NextId == nextId)
                    Finish(worker, "could not apply the selected medicine");
                else
                {
                    treatment.Application = new DoAfterId(worker, nextId);
                    treatment.DamageAtApplication = damage;
                }
                continue;
            }

            if (TryComp(worker, out NPCSteeringComponent? steering))
            {
                if (steering.Status is SteeringStatus.NoPath or SteeringStatus.InRange)
                    Finish(worker, "could not reach the patient");
            }
            else if (_pathfinding.GetPoly(transform.Coordinates) != null && _pathfinding.GetPoly(claim.Target) != null)
            {
                _steering.Register(worker, claim.Target).Range = TreatmentRange;
            }
        }
    }

    private Entity<HealingComponent>? FindMedicine(EntityUid worker, EntityUid patient)
    {
        if (!TryComp(patient, out InjurableComponent? injury))
            return null;

        foreach (var item in _hands.EnumerateHeld(worker))
        {
            if ((_tags.HasTag(item, BruisePack) || _tags.HasTag(item, Ointment)) &&
                TryComp(item, out HealingComponent? healing) && _stacks.GetCount(item) > 0 &&
                (injury.DamageContainer is not { } container || healing.DamageContainers == null ||
                 healing.DamageContainers.Contains(container)) &&
                GetTreatableDamage(patient, healing.Damage) > FixedPoint2.Zero)
                return (item, healing);
        }

        return null;
    }

    private FixedPoint2 GetTreatableDamage(EntityUid patient, DamageSpecifier healingProfile)
    {
        var damage = _damage.GetAllDamage(patient).DamageDict;
        var total = FixedPoint2.Zero;
        foreach (var (type, amount) in healingProfile.DamageDict)
        {
            if (amount < FixedPoint2.Zero)
                total += damage.GetValueOrDefault(type);
        }
        return total;
    }

    private void Finish(EntityUid worker, string outcome)
    {
        Log.Info($"Living Station medical treatment {outcome} for {ToPrettyString(worker)}.");
        RemComp<MedicalTreatmentComponent>(worker);
    }

    private void OnTreatmentShutdown(EntityUid worker, MedicalTreatmentComponent treatment, ComponentShutdown args)
    {
        if (_doAfter.IsRunning(treatment.Application))
            _doAfter.Cancel(treatment.Application);

        _work.Release(worker);
        _steering.Unregister(worker);
        RemComp<ActiveNPCComponent>(worker);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.Old != GameRunLevel.InRound || ev.New == GameRunLevel.InRound)
            return;

        var treatments = AllEntityQuery<MedicalTreatmentComponent>();
        while (treatments.MoveNext(out var worker, out _))
            RemComp<MedicalTreatmentComponent>(worker);
    }
}
