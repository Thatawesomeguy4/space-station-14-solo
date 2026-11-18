using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

/// <summary>
/// One active order per worker and one owner per target. Each job releases its claim on shutdown.
/// </summary>
[RegisterComponent]
public sealed partial class LivingStationWorkClaimComponent : Component
{
    public EntityCoordinates Target;
    public TimeSpan Started;
    public TimeSpan Deadline;
    public EntityUid? Requester;
}

[RegisterComponent]
public sealed partial class LivingStationWorkHistoryComponent : Component
{
    public string? LastOutcome;
    public string? LastFailure;
}

public sealed partial class LivingStationWorkSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private LivingStationIdleSystem _idle = default!;

    internal int Assigned { get; private set; }
    internal int Released { get; private set; }
    internal int RejectedBusy { get; private set; }
    internal int RejectedReserved { get; private set; }
    internal int Completed { get; private set; }
    internal int Failed { get; private set; }

    public override void Initialize()
    {
        SubscribeLocalEvent<LivingStationWorkClaimComponent, ComponentShutdown>((uid, claim, args) => Released++);
    }

    // Called by the crew spawner before any startup assignments, independently of event subscriber order.
    internal void ResetCounters() => Assigned = Released = RejectedBusy = RejectedReserved = Completed = Failed = 0;

    internal bool TryAssign(EntityUid worker, EntityCoordinates target, TimeSpan timeout, EntityUid? requester = null)
    {
        if (HasComp<LivingStationWorkClaimComponent>(worker))
        {
            RejectedBusy++;
            return false;
        }

        var claims = AllEntityQuery<LivingStationWorkClaimComponent>();
        while (claims.MoveNext(out var claim))
        {
            if (claim.Target == target)
            {
                RejectedReserved++;
                return false;
            }
        }

        var assignment = AddComp<LivingStationWorkClaimComponent>(worker);
        _idle.Stop(worker);
        assignment.Target = target;
        assignment.Requester = requester;
        assignment.Started = _timing.CurTime;
        assignment.Deadline = _timing.CurTime + timeout;
        Assigned++;
        return true;
    }

    internal void RecordOutcome(EntityUid worker, string outcome, bool success)
    {
        if (!HasComp<LivingStationWorkClaimComponent>(worker))
            return;

        var history = EnsureComp<LivingStationWorkHistoryComponent>(worker);
        history.LastOutcome = outcome;
        if (success)
            Completed++;
        else
        {
            history.LastFailure = outcome;
            Failed++;
        }
    }

    internal void Release(EntityUid worker)
    {
        RemComp<LivingStationWorkClaimComponent>(worker);
        _idle.Stop(worker);
    }
}
