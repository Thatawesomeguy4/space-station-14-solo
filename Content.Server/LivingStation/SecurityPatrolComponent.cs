using Robust.Shared.Map;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class SecurityPatrolPointComponent : Component
{
    [DataField]
    public int Order;
}

[RegisterComponent]
public sealed partial class SecurityPatrolComponent : Component
{
    public List<EntityUid> Waypoints = new();
    public int NextWaypoint;
    public TimeSpan ResumePatrolAt;
    public EntityCoordinates? LastInspected;
}
