using System.Numerics;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Robust.Shared.Map;

namespace Content.Server.LivingStation;

[RegisterComponent]
public sealed partial class MedicalTreatmentPointComponent : Component
{
    [DataField(required: true)]
    public Vector2 WorkerOffset;
}

[RegisterComponent]
public sealed partial class LivingStationMedicComponent : Component
{
    public EntityCoordinates? TreatmentPoint;
    public EntityUid? ChemistryKit;
    public TimeSpan NextChemistryAttempt;
    public readonly List<EntityUid> MedicationBeakers = new();
}

[RegisterComponent]
public sealed partial class MedicalTreatmentComponent : Component
{
    public EntityCoordinates TreatmentPoint;
    public EntityUid Medicine;
    public DamageSpecifier HealingProfile = default!;
    public FixedPoint2 DamageAtApplication;
    public DoAfterId? Application;
}
