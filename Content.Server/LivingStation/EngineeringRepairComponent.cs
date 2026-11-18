using System.Numerics;
using Content.Shared.DoAfter;
using Robust.Shared.Localization;
using Robust.Shared.Map;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class EngineeringRepairFixtureComponent : Component
{
    [DataField(required: true)]
    public Vector2 WorkerOffset;

    [DataField(required: true)]
    public Vector2 ApproachOffset;
}

[RegisterComponent]
public sealed partial class EngineeringRepairComponent : Component
{
    public bool Incident;
    public LocId Stage = "living-station-engineering-stage-travel";
    public EntityUid? Welder;
    public DoAfterId? Welding;
    public EntityUid? CarriedSupply;
    public EntityUid? StoredTool;
    public bool Inspected;
    public int NextApproachWaypoint;
    public bool CableInstalled;
    public TimeSpan VerifyPowerAfter;
    public EntityUid? ConnectedTank;
    public EntityUid? Requester;
    public Queue<EntityCoordinates>? LightApproaches;
}

[RegisterComponent]
public sealed partial class LivingStationEngineerComponent : Component;

[RegisterComponent]
public sealed partial class EngineeringServicePointComponent : Component
{
    [DataField]
    public bool CargoPower;

    [DataField]
    public List<Vector2> ApproachWaypoints = new();

    [DataField]
    public Vector2 ApproachOffset;

    // An exposed LV segment known to feed this APC's local loads.
    [DataField]
    public Vector2? CableOffset;

    [DataField]
    public Vector2? PowerCheckOffset;

    // A known hull tile adjacent to this alarm's compartment.
    [DataField]
    public Vector2? HullOffset;
}
