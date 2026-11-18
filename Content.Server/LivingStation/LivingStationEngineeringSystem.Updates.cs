using Content.Server.Chat.Systems;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationEngineeringSystem
{
    private static readonly TimeSpan PersonalUpdateCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersonalUpdateLifetime = TimeSpan.FromMinutes(1);
    private readonly Dictionary<EntityUid, RepairUpdate> _repairUpdates = new();
    private readonly Dictionary<EntityUid, TimeSpan> _nextPersonalUpdate = new();

    [Dependency] private ChatSystem _chat = default!;

    private sealed record RepairUpdate(EntityUid Requester, NetUserId User, string Message, TimeSpan Expires);

    private void QueuePersonalUpdate(EntityUid worker, EntityUid requester, string result)
    {
        if (!_recentRepairs.TryGetValue(requester, out var request) || request.User is not { } user ||
            !TryComp<ActorComponent>(requester, out var actor) || actor.PlayerSession.UserId != user)
            return;

        // Keep the latest outcome per engineer while waiting for a nearby requester or the speech cooldown.
        _repairUpdates[worker] = new(requester, user,
            Loc.GetString("living-station-engineering-personal-update", ("result", result)),
            _timing.CurTime + PersonalUpdateLifetime);
    }

    private void SendPersonalUpdates()
    {
        foreach (var (worker, update) in _repairUpdates)
        {
            if (_timing.CurTime >= update.Expires || TerminatingOrDeleted(worker) ||
                TerminatingOrDeleted(update.Requester) ||
                !TryComp<ActorComponent>(update.Requester, out var actor) || actor.PlayerSession.UserId != update.User)
            {
                _repairUpdates.Remove(worker);
                continue;
            }

            if (!_configuration.GetCVar(CCVars.LivingStationEnabled) ||
                _nextPersonalUpdate.TryGetValue(worker, out var next) && _timing.CurTime < next ||
                !CanWorkMaintenance(worker) || !_mobState.IsAlive(update.Requester) || !_blocker.CanSpeak(worker) ||
                !_interaction.InRangeUnobstructed(worker, update.Requester, 4f))
                continue;

            _chat.TrySendInGameICMessage(worker, update.Message, InGameICChatType.Speak, ChatTransmitRange.Normal,
                hideLog: true, checkRadioPrefix: false);
            _nextPersonalUpdate[worker] = _timing.CurTime + PersonalUpdateCooldown;
            _repairUpdates.Remove(worker);
        }
    }
}
