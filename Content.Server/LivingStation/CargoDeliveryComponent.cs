using Robust.Shared.Prototypes;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class CargoDeliveryPickupComponent : Component
{
    public EntityUid? Worker;
    public EntityUid Dropoff;
    public bool AutomaticHauling;
    public TimeSpan NextReturnAttempt;
    internal readonly List<CargoRequisition> Requisitions = new();
}

internal sealed record CargoRequisition(EntityUid Requester, EntProtoId Product);

[RegisterComponent]
public sealed partial class CargoDepartmentPalletComponent : Component
{
    [DataField(required: true)]
    public CargoDepartment Department;
}

public enum CargoDepartment : byte
{
    Medical,
    Engineering,
    Janitorial,
    Returns,
}

/// <summary>
/// Execution state for the worker's active delivery.
/// </summary>
[RegisterComponent]
public sealed partial class CargoDeliveryComponent : Component
{
    public EntityUid Crate;
    public EntityUid Dropoff;
    public bool Pulling;
    public bool Completed;
    public bool EmptyReturn;
    internal CargoRequisition? Requisition;
}
