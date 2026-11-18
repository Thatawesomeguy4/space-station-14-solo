using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Robust.Shared.Player;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class LivingStationConversationPartnerComponent : Component
{
    public LivingStationDepartment Department;
    public TimeSpan NextConversation;
}

public sealed partial class LivingStationBartenderDialogueSystem
{
    private static readonly TimeSpan NpcConversationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NpcPartnerCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NpcConversationPause = TimeSpan.FromSeconds(15);
    private TimeSpan _nextNpcConversationCheck;
    private TimeSpan _nextNpcConversation;
    private NpcConversation? _npcConversation;
    private NpcConversationReply? _npcReply;

    private sealed record NpcConversation(EntityUid First, EntityUid Second, int Round, string? PreviousLine,
        TimeSpan NextTurn, TimeSpan Expires,
        LivingStationWorkClaimComponent? FirstWork, LivingStationWorkClaimComponent? SecondWork);

    private sealed record NpcConversationReply(EntityUid Speaker, DepartmentHeadContext Facts, long Started,
        CancellationTokenSource Cancellation, Task<BartenderReply> Reply);

    private void InitializeNpcConversations()
    {
        SubscribeLocalEvent<EntitySpokeEvent>(OnPlayerInterruptsConversation);
        Subs.CVar(_config, CCVars.LivingStationNpcConversationsEnabled, enabled =>
        {
            if (!enabled)
                CancelNpcConversation();
        });
    }

    internal void RegisterConversationPartner(EntityUid worker, LivingStationDepartment department)
    {
        var partner = AddComp<LivingStationConversationPartnerComponent>(worker);
        partner.Department = department;
        partner.NextConversation = _timing.CurTime + NpcConversationInterval;
    }

    private bool CanChatWithCrew(EntityUid npc) =>
        !TerminatingOrDeleted(npc) && !Paused(npc) && !HasComp<ActorComponent>(npc) &&
        HasComp<LivingStationConversationPartnerComponent>(npc) &&
        _mobState.IsAlive(npc) && _blocker.CanSpeak(npc);

    private bool CanChatTogether(EntityUid first, EntityUid second) =>
        first != second && CanChatWithCrew(first) && CanChatWithCrew(second) &&
        _interaction.InRangeUnobstructed(first, second, 3f);

    private void UpdateNpcConversations()
    {
        if (!Enabled || !_config.GetCVar(CCVars.LivingStationNpcConversationsEnabled) ||
            _ticker.RunLevel != GameRunLevel.InRound)
        {
            CancelNpcConversation();
            return;
        }

        if (_npcConversation is { } conversation)
        {
            if (conversation.Round != _ticker.RoundId || _timing.CurTime >= conversation.Expires ||
                !CanChatTogether(conversation.First, conversation.Second) ||
                CompOrNull<LivingStationWorkClaimComponent>(conversation.First) != conversation.FirstWork ||
                CompOrNull<LivingStationWorkClaimComponent>(conversation.Second) != conversation.SecondWork)
            {
                CancelNpcConversation();
                return;
            }

            if (_npcReply is { } pending)
            {
                if (!pending.Reply.IsCompleted)
                    return;

                _npcReply = null;
                pending.Cancellation.Dispose();
                var context = ReadNpcContext(pending.Speaker);
                if (!pending.Reply.IsCompletedSuccessfully || context != pending.Facts ||
                    Stopwatch.GetElapsedTime(pending.Started) >= OllamaBartenderClient.Deadline)
                {
                    CancelNpcConversation();
                    return;
                }

                var reply = pending.Reply.GetAwaiter().GetResult();
                Log.Debug($"Living Station crew conversation reply {reply.Kind} in round {conversation.Round}.");
                if (reply.Kind != BartenderReplyKind.Speak)
                {
                    CancelNpcConversation();
                    return;
                }

                Speak(pending.Speaker, reply.Speech!);
                if (conversation.PreviousLine != null)
                {
                    CancelNpcConversation();
                    return;
                }

                _npcConversation = conversation with { PreviousLine = reply.Speech, NextTurn = _timing.CurTime + Cooldown };
                return;
            }

            if (_pending == null && _timing.CurTime >= conversation.NextTurn)
                RequestNpcLine(conversation);
            return;
        }

        if (_pending != null || _timing.CurTime < _nextRequest || _timing.CurTime < _nextNpcConversation ||
            _timing.CurTime < _nextNpcConversationCheck)
            return;

        _nextNpcConversationCheck = _timing.CurTime + TimeSpan.FromSeconds(5);
        var partners = EntityQueryEnumerator<LivingStationConversationPartnerComponent>();
        while (partners.MoveNext(out var first, out var firstPartner))
        {
            if (firstPartner.NextConversation > _timing.CurTime || !CanChatWithCrew(first))
                continue;

            var others = EntityQueryEnumerator<LivingStationConversationPartnerComponent>();
            while (others.MoveNext(out var second, out var secondPartner))
            {
                if (secondPartner.NextConversation > _timing.CurTime || !CanChatTogether(first, second))
                    continue;

                BeginNpcConversation(first, second, firstPartner, secondPartner);
                return;
            }
        }
    }

    private void BeginNpcConversation(EntityUid first, EntityUid second,
        LivingStationConversationPartnerComponent firstPartner, LivingStationConversationPartnerComponent secondPartner)
    {
        firstPartner.NextConversation = secondPartner.NextConversation = _timing.CurTime + NpcPartnerCooldown;
        _nextNpcConversation = _timing.CurTime + NpcConversationInterval;
        _npcConversation = new(first, second, _ticker.RoundId, null, _timing.CurTime,
            _timing.CurTime + NpcConversationPause,
            CompOrNull<LivingStationWorkClaimComponent>(first), CompOrNull<LivingStationWorkClaimComponent>(second));
        PauseIdleWalk(first);
        PauseIdleWalk(second);
        RequestNpcLine(_npcConversation);
    }

    private void PauseIdleWalk(EntityUid npc)
    {
        if (!HasComp<LivingStationWorkClaimComponent>(npc) && !HasComp<NPCSteeringComponent>(npc) &&
            TryComp<LivingStationIdleRouteComponent>(npc, out var idle) &&
            idle.NextWalk < _timing.CurTime + NpcConversationPause)
            idle.NextWalk = _timing.CurTime + NpcConversationPause;
    }

    private DepartmentHeadContext ReadNpcContext(EntityUid npc) =>
        EntityManager.System<LivingStationIncidentSystem>()
            .ReadCrewConversationContext(npc, Comp<LivingStationConversationPartnerComponent>(npc).Department);

    private void RequestNpcLine(NpcConversation conversation)
    {
        var speaker = conversation.PreviousLine == null ? conversation.First : conversation.Second;
        var listener = speaker == conversation.First ? conversation.Second : conversation.First;
        var facts = ReadNpcContext(speaker);
        var listenerRole = Name(listener);
        var transport = Transport;
        var model = _config.GetCVar(CCVars.LivingStationOllamaModel);
        var cancellation = new CancellationTokenSource(OllamaBartenderClient.Deadline);
        var token = cancellation.Token;
        var started = Stopwatch.GetTimestamp();
        var reply = Task.Run(() => transport.AskNpcConversationAsync(model, facts, listenerRole, conversation.PreviousLine, token));
        _npcReply = new(speaker, facts, started, cancellation, reply);
    }

    private void OnPlayerInterruptsConversation(EntitySpokeEvent ev)
    {
        if (_npcConversation is not { } conversation || ev.Channel != null || ev.ObfuscatedMessage != null ||
            !HasComp<ActorComponent>(ev.Source))
            return;

        if (TerminatingOrDeleted(conversation.First) || TerminatingOrDeleted(conversation.Second) ||
            _interaction.InRangeUnobstructed(ev.Source, conversation.First, ConversationRange) ||
            _interaction.InRangeUnobstructed(ev.Source, conversation.Second, ConversationRange))
            CancelNpcConversation();
    }

    private void CancelNpcConversation()
    {
        if (_npcReply is { } reply)
        {
            reply.Cancellation.Cancel();
            reply.Cancellation.Dispose();
            _npcReply = null;
        }

        _npcConversation = null;
    }
}
