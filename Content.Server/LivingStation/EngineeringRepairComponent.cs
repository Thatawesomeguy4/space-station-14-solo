using System.Numerics;

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
public sealed partial class EngineeringRepairComponent : Component;
