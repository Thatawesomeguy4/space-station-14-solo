using System.IO;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators.Interactions;
using Content.Server.Radio.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Microsoft.Data.Sqlite;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Localization;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationIncidentSystem : EntitySystem
{
    private static readonly ProtoId<RadioChannelPrototype> CommandChannel = "Command";
    private static readonly TimeSpan ReplyCooldown = TimeSpan.FromSeconds(2);

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private IGameMapManager _maps = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPoweredLightSystem _lights = default!;
    [Dependency] private LivingStationBartenderDialogueSystem _dialogue = default!;

    internal LivingStationMemoryStore? Memory { get; set; }
    internal LightIncidentNotes Notes { get; private set; }
    internal Task Writes { get; private set; } = Task.CompletedTask;
    private EntityUid? _engineer;
    private EntityUid? _security;
    private EntityUid? _fixture;
    private readonly Dictionary<string, EntityUid> _otherHeads = new(StringComparer.OrdinalIgnoreCase);
    private Task<LightIncidentNotes>? _load;
    private long? _observationThisRound;
    private IncidentReport _sending;
    private IncidentReport _rumor;
    private TimeSpan _nextReply;

    private enum IncidentReport
    {
        None,
        ObservedFailure,
        Unverified,
        VerifiedWorking,
        VerifiedFailed,
    }

    internal bool RumorVerified => _rumor is IncidentReport.VerifiedWorking or IncidentReport.VerifiedFailed;

    internal void RegisterDepartmentHead(string title, EntityUid head) => _otherHeads[title] = head;

    private bool Enabled => _config.GetCVar(CCVars.LivingStationEnabled) && _ticker.RunLevel == GameRunLevel.InRound;

    public override void Initialize()
    {
        SubscribeLocalEvent<EntitySpokeEvent>(OnPlayerSpoke);
        SubscribeLocalEvent<LivingStationIncidentListenerComponent, HeadsetRadioReceiveRelayEvent>(OnReportReceived);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
    }

    internal void RegisterChiefEngineer(EntityUid engineer, EntityUid fixture)
    {
        if (_maps.GetSelectedMap()?.ID != "Saltern")
            return;

        _engineer = engineer;
        _fixture = fixture;
        if (Memory == null && _resources.UserData.RootDir is { } root)
            Memory = new LivingStationMemoryStore(Path.Combine(root, LivingStationMemoryStore.FileName));

        if (Memory is { } memory)
        {
            var previousWrites = Writes;
            _load = Task.Run(async () =>
            {
                await previousWrites.ConfigureAwait(false);
                try
                {
                    return memory.ReadLightIncident();
                }
                catch (SqliteException ex)
                {
                    Log.Warning($"Could not load Living Station light incident. SQLite error {ex.SqliteErrorCode}.");
                    return default;
                }
            });
        }
    }

    internal void RegisterHeadOfSecurity(EntityUid officer)
    {
        if (_maps.GetSelectedMap()?.ID != "Saltern")
            return;

        _security = officer;
        EnsureComp<LivingStationIncidentListenerComponent>(officer);
    }

    internal void BeginLightIncident() => _observationThisRound = null;

    internal bool TryStartRumor()
    {
        if (!Enabled || !Available(_engineer) || !Available(_security) || !TryComp<PoweredLightComponent>(_fixture, out _))
            return false;

        SendReport(_engineer!.Value, IncidentReport.Unverified, "living-station-rumor-radio");
        return _rumor == IncidentReport.Unverified;
    }

    internal void EndRumor() => _rumor = IncidentReport.None;

    private bool Available(EntityUid? npc) =>
        npc is { } uid && _mobState.IsAlive(uid) && !HasComp<ActorComponent>(uid);

    internal void ObserveBrokenLight(EntityUid engineer, EntityUid fixture)
    {
        if (!Enabled || engineer != _engineer || fixture != _fixture || _observationThisRound != null)
            return;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        _observationThisRound = now;
        Notes = Notes with { ObservedAt = now };
        Save(LightIncidentWitness.ChiefEngineer, now);
        SendReport(engineer, IncidentReport.ObservedFailure, "living-station-incident-radio");
    }

    private void SendReport(EntityUid engineer, IncidentReport report, string speech)
    {
        if (!_blocker.CanSpeak(engineer) ||
            !TryComp<WearingHeadsetComponent>(engineer, out var worn) ||
            !TryComp<EncryptionKeyHolderComponent>(worn.Headset, out var keys) || !keys.Channels.Contains(CommandChannel))
            return;

        _sending = report;
        try
        {
            _radio.SendRadioMessage(engineer, Loc.GetString(speech), ProtoMan.Index(CommandChannel), worn.Headset);
        }
        finally
        {
            _sending = IncidentReport.None;
        }
    }

    private void OnReportReceived(Entity<LivingStationIncidentListenerComponent> officer, ref HeadsetRadioReceiveRelayEvent ev)
    {
        if (_sending == IncidentReport.None || officer.Owner != _security || ev.RelayedEvent.MessageSource != _engineer ||
            ev.RelayedEvent.Channel.ID != CommandChannel.Id || !Available(officer.Owner))
            return;

        if (_sending != IncidentReport.ObservedFailure)
        {
            _rumor = _sending;
            return;
        }

        if (_observationThisRound is not { } observedAt || Notes.ReportedAt == observedAt)
            return;

        Notes = Notes with { ReportedAt = observedAt };
        Save(LightIncidentWitness.HeadOfSecurity, observedAt);
    }

    private void Save(LightIncidentWitness witness, long observedAt)
    {
        if (Memory is not { } memory)
            return;

        var previous = Writes;
        Writes = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                memory.SaveLightIncident(witness, observedAt);
            }
            catch (SqliteException ex)
            {
                Log.Warning($"Could not save Living Station light incident. SQLite error {ex.SqliteErrorCode}.");
            }
        });
    }

    public override void Update(float frameTime)
    {
        if (_load is not { IsCompleted: true } load)
            return;

        _load = null;
        if (!load.IsCompletedSuccessfully)
        {
            Log.Warning("Could not load Living Station light incident.");
            return;
        }

        var saved = load.GetAwaiter().GetResult();
        Notes = new(Notes.ObservedAt ?? saved.ObservedAt, Notes.ReportedAt ?? saved.ReportedAt);
    }

    private void OnPlayerSpoke(EntitySpokeEvent ev)
    {
        var customer = ev.Source;
        if (!Enabled || _timing.CurTime < _nextReply || ev.Channel != null || ev.ObfuscatedMessage != null ||
            !HasComp<ActorComponent>(customer))
            return;

        if (LivingStationBartenderDialogueSystem.TryGetAddressedMessage(ev.Message, "chief engineer", out var question))
            Reply(_engineer, customer, question, LightIncidentWitness.ChiefEngineer);
        else if (LivingStationBartenderDialogueSystem.TryGetAddressedMessage(ev.Message, "head of security", out question))
            Reply(_security, customer, question, LightIncidentWitness.HeadOfSecurity);
        else
        {
            foreach (var (title, head) in _otherHeads)
            {
                if (!LivingStationBartenderDialogueSystem.TryGetAddressedMessage(ev.Message, title, out question))
                    continue;

                if (CanTalk(head, customer))
                    Ask(head, customer, question, () => ReadDepartmentContext(head, title));
                break;
            }
        }
    }

    private bool CanTalk(EntityUid npc, EntityUid customer) =>
        npc != customer && Available(npc) && _mobState.IsAlive(customer) && _blocker.CanSpeak(npc) &&
        _interaction.InRangeUnobstructed(npc, customer, 4f);

    private void Reply(EntityUid? speaker, EntityUid customer, string question, LightIncidentWitness witness)
    {
        if (speaker is not { } npc || !CanTalk(npc, customer))
            return;

        var remembered = witness == LightIncidentWitness.ChiefEngineer ? Notes.ObservedAt : Notes.ReportedAt;
        var normalized = question.Trim().TrimEnd('?', '.', '!').ToLowerInvariant();
        if (witness == LightIncidentWitness.ChiefEngineer &&
            normalized is "what are you working on" or "what are you working on right now" or
                "what is your current assignment" or "what's your current assignment")
        {
            _nextReply = _timing.CurTime + ReplyCooldown;
            Say(npc, _dialogue.EngineeringFallback(ReadEngineeringFacts(npc, customer)));
            return;
        }

        if (witness == LightIncidentWitness.ChiefEngineer &&
            normalized is "what is the status of my repair" or "what's the status of my repair" or "repair status")
        {
            _nextReply = _timing.CurTime + ReplyCooldown;
            Say(npc, ReadRepairStatus(customer) ?? Loc.GetString("living-station-engineering-followup-none"));
            return;
        }

        var speech = normalized switch
        {
            "what happened" or "what do you remember" when remembered != null =>
                witness == LightIncidentWitness.ChiefEngineer ? "living-station-incident-observed" : "living-station-incident-reported",
            "what happened" or "what do you remember" =>
                _load != null ? "living-station-incident-loading" : "living-station-incident-unknown",
            "who broke it" or "who broke the light" => "living-station-incident-unknown-cause",
            "is the light fixed" => CurrentLightStatus(),
            "what have you heard" when witness == LightIncidentWitness.HeadOfSecurity => _rumor switch
            {
                IncidentReport.Unverified => "living-station-rumor-reported",
                IncidentReport.VerifiedWorking => "living-station-rumor-cleared",
                IncidentReport.VerifiedFailed => "living-station-rumor-confirmed",
                _ => "living-station-rumor-none",
            },
            "can you check the light" when witness == LightIncidentWitness.ChiefEngineer => VerifyRumor(npc),
            _ => null,
        };
        if (speech == null)
        {
            if (witness == LightIncidentWitness.ChiefEngineer)
                Ask(npc, customer, question, () => ReadEngineeringContext(npc, customer));
            else
                Ask(npc, customer, question, () => ReadDepartmentContext(npc, "Head of Security"));
            return;
        }

        _nextReply = _timing.CurTime + ReplyCooldown;
        Say(npc, Loc.GetString(speech));
    }

    private void Ask(EntityUid npc, EntityUid customer, string question, Func<DepartmentHeadContext> readFacts)
    {
        _nextReply = _timing.CurTime + ReplyCooldown;
        if (!_dialogue.TryAskDepartmentHead(npc, customer, question, readFacts))
            Say(npc, readFacts().Fallback);
    }

    private bool HasWork(EntityUid npc) =>
        TryComp<LivingStationWorkClaimComponent>(npc, out var claim) && Exists(claim.Target.EntityId);

    private string? ReadRepairStatus(EntityUid customer) =>
        EntityManager.System<LivingStationEngineeringSystem>().GetRecentRepairStatus(customer);

    private DepartmentHeadContext ReadEngineeringContext(EntityUid npc, EntityUid customer)
    {
        var facts = ReadEngineeringFacts(npc, customer);
        return new("Chief Engineer", facts, facts.RecentRepair ?? _dialogue.EngineeringFallback(facts));
    }

    private ChiefEngineerContext ReadEngineeringFacts(EntityUid npc, EntityUid customer)
    {
        var working = HasWork(npc);
        return new(working, Loc.GetString(CurrentLightStatus()),
            Notes.ObservedAt != null,
            working && TryComp<EngineeringRepairComponent>(npc, out var order) ? Loc.GetString(order.Stage) : null,
            ReadRepairStatus(customer), ReadRoutine(npc));
    }

    private DepartmentHeadContext ReadDepartmentContext(EntityUid npc, string title)
    {
        var working = HasWork(npc);
        var key = title switch
        {
            "Head of Security" when working => "living-station-head-security-busy",
            "Head of Security" => "living-station-head-security-idle",
            "Quartermaster" when working => "living-station-head-cargo-busy",
            "Quartermaster" => "living-station-head-cargo-idle",
            "Chief Medical Officer" when working => "living-station-head-medical-busy",
            "Chief Medical Officer" => "living-station-head-medical-idle",
            "Head of Personnel" => "living-station-head-personnel",
            _ => "living-station-head-science",
        };
        var status = Loc.GetString(key);
        if (title == "Head of Security" && Notes.ReportedAt != null)
            status += " " + Loc.GetString("living-station-incident-reported");

        var routine = working ? null : ReadRoutine(npc);
        return new(title, new DepartmentStatus(working, status, routine), routine == null ? status : status + " " + routine);
    }

    private string? ReadRoutine(EntityUid npc)
    {
        if (HasWork(npc) || !TryComp<LivingStationIdleRouteComponent>(npc, out var route))
            return null;

        return Loc.GetString(route.Activity switch
        {
            LivingStationRoutine.StockroomVisit => "living-station-routine-stockroom",
            LivingStationRoutine.BarBreak => route.Walking ? "living-station-routine-going-to-bar" : "living-station-routine-bar",
            LivingStationRoutine.ReturningHome => "living-station-routine-returning",
            _ => "living-station-routine-rounds",
        });
    }

    internal DepartmentHeadContext ReadCrewConversationContext(EntityUid npc, LivingStationDepartment department)
    {
        var context = ReadConversationRole(npc, department);
        var working = TryComp<LivingStationWorkClaimComponent>(npc, out var claim) && Exists(claim.Target.EntityId);
        var target = working ? Name(claim!.Target.EntityId) : null;
        string? stage = null;
        if (working)
        {
            LocId stageKey = department switch
            {
                LivingStationDepartment.Engineering when TryComp<EngineeringRepairComponent>(npc, out var repair) => repair.Stage,
                LivingStationDepartment.Cargo => "living-station-conversation-cargo",
                LivingStationDepartment.Medical => "living-station-conversation-medical",
                LivingStationDepartment.Security => "living-station-conversation-security",
                LivingStationDepartment.Service when HasComp<BarServiceOrderComponent>(npc) => "living-station-conversation-service",
                _ => "living-station-conversation-working",
            };
            stage = Loc.GetString(stageKey);
        }
        else if (TryComp<HTNComponent>(npc, out var htn) && htn.Enabled &&
            htn.Plan is { } plan && plan.Index >= 0 && plan.Index < plan.Tasks.Count)
        {
            var activity = plan.CurrentOperator switch
            {
                MopPuddleOperator => "living-station-conversation-mopping",
                BagTrashOperator => "living-station-conversation-trash",
                _ => null,
            };
            if (activity != null)
            {
                working = true;
                stage = Loc.GetString(activity);
            }
        }

        var incident = npc == _engineer && Notes.ObservedAt != null ? Loc.GetString("living-station-incident-observed") :
            npc == _security && Notes.ReportedAt != null ? Loc.GetString("living-station-incident-reported") : null;
        return context with { Facts = new CrewConversationFacts(working, target, stage, working ? null : ReadRoutine(npc), incident) };
    }

    private DepartmentHeadContext ReadConversationRole(EntityUid npc, LivingStationDepartment department)
    {
        if (npc == _engineer)
            return ReadEngineeringContext(npc, npc);
        if (npc == _security)
            return ReadDepartmentContext(npc, "Head of Security");
        foreach (var (title, head) in _otherHeads)
        {
            if (head == npc)
                return ReadDepartmentContext(npc, title);
        }

        var role = MetaData(npc).EntityPrototype?.ID == "MobLivingStationBartender" ? "Bartender" : department switch
        {
            LivingStationDepartment.Cargo => "Cargo technician",
            LivingStationDepartment.Engineering => "Station engineer",
            LivingStationDepartment.Medical => "Station medic",
            LivingStationDepartment.Security => "Security officer",
            LivingStationDepartment.Science => "Research crew member",
            _ => "Service crew member",
        };
        var routine = ReadRoutine(npc);
        var status = Loc.GetString("living-station-idle-conversation-status", ("department", department.ToString()));
        return new(role, new DepartmentStatus(false, status, routine), status);
    }

    private void Say(EntityUid npc, string message) =>
        _chat.TrySendInGameICMessage(npc, message, InGameICChatType.Speak, ChatTransmitRange.Normal,
            hideLog: true, checkRadioPrefix: false);

    private string VerifyRumor(EntityUid engineer)
    {
        if (_rumor != IncidentReport.Unverified)
            return "living-station-rumor-none";

        if (_fixture is not { } fixture || !_interaction.InRangeUnobstructed(engineer, fixture) ||
            !TryComp<PoweredLightComponent>(fixture, out var light))
            return "living-station-rumor-cannot-check";

        if (TryComp<LightBulbComponent>(_lights.GetBulb(fixture, light), out var bulb) && bulb.State == LightBulbState.Normal)
            SendReport(engineer, IncidentReport.VerifiedWorking, "living-station-rumor-working-radio");
        else
            SendReport(engineer, IncidentReport.VerifiedFailed, "living-station-rumor-failed-radio");

        return CurrentLightStatus();
    }

    private string CurrentLightStatus()
    {
        if (_fixture is not { } fixture || !TryComp<PoweredLightComponent>(fixture, out var light))
            return "living-station-incident-no-fixture";

        if (light.CurrentLit)
            return "living-station-incident-lit";

        return TryComp<LightBulbComponent>(_lights.GetBulb(fixture, light), out var bulb) && bulb.State == LightBulbState.Normal
            ? "living-station-incident-unlit"
            : "living-station-incident-broken";
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New == GameRunLevel.InRound)
            return;

        _engineer = null;
        _security = null;
        _otherHeads.Clear();
        _fixture = null;
        _observationThisRound = null;
        EndRumor();
        _load = null;
        _nextReply = TimeSpan.Zero;
        Notes = default;
    }
}

[RegisterComponent]
internal sealed partial class LivingStationIncidentListenerComponent : Component;
