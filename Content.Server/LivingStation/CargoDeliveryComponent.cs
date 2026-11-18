using System.Numerics;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class CargoDeliveryPickupComponent : Component
{
    public readonly List<EntityUid> Workers = new();
    public EntityUid Dropoff;
    public bool AutomaticHauling;
    public TimeSpan NextReturnAttempt;
    public TimeSpan NextSteelExport;
    public TimeSpan NextOreHaul;
    public bool OreLoaded;
    public LocId? OreIssue;
    internal readonly List<CargoRequisition> Requisitions = new();
}

internal sealed record CargoRequisition(EntityUid Requester, EntProtoId Product);

[RegisterComponent]
public sealed partial class CargoStockroomComponent : Component
{
    [DataField(required: true)]
    public CargoDepartment Department;

    public TimeSpan NextRequisition;
}

[RegisterComponent]
public sealed partial class CargoDepartmentPalletComponent : Component
{
    [DataField(required: true)]
    public CargoDepartment Department;

    [DataField]
    public List<Vector2> ApproachOffsets = new();
}

public enum CargoDepartment : byte
{
    Medical,
    Engineering,
    Janitorial,
    Returns,
    Export,
    OreIntake,
    OreProcessing,
    Materials,
    Science,
}

[RegisterComponent]
public sealed partial class CargoOreProcessorComponent : Component;

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
    public int NavigationRetries;
    public int NextApproach;
    internal CargoRequisition? Requisition;
}
