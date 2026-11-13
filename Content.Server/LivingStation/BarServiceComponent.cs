using System.Numerics;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class BarServiceCounterComponent : Component
{
    [DataField(required: true)]
    public Vector2 StaffOffset;

    [DataField(required: true)]
    public Vector2 StockOffset;

    [DataField(required: true)]
    public Vector2 StockApproachOffset;

    public EntityUid? Bartender;
}

[RegisterComponent]
public sealed partial class BarServiceOrderComponent : Component
{
    public EntityUid Drink;
}
