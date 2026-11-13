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
    public TimeSpan Deadline;
}

public sealed partial class LivingStationWorkSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;

    internal bool TryAssign(EntityUid worker, EntityCoordinates target, TimeSpan timeout)
    {
        if (HasComp<LivingStationWorkClaimComponent>(worker))
            return false;

        var claims = AllEntityQuery<LivingStationWorkClaimComponent>();
        while (claims.MoveNext(out var claim))
        {
            if (claim.Target == target)
                return false;
        }

        var assignment = AddComp<LivingStationWorkClaimComponent>(worker);
        assignment.Target = target;
        assignment.Deadline = _timing.CurTime + timeout;
        return true;
    }

    internal void Release(EntityUid worker)
    {
        RemComp<LivingStationWorkClaimComponent>(worker);
    }
}
