using System.Collections.Immutable;
using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public enum LivingStationDepartment
{
    Service,
    Cargo,
    Engineering,
    Medical,
    Security,
    Science,
}

public static class LivingStationStaffing
{
    public const string Command = "livingstationstaffing";
    public static IReadOnlyList<CVarDef<int>> Departments { get; } = ImmutableArray.CreateRange(new CVarDef<int>[]
    {
        CCVars.LivingStationServiceStaff,
        CCVars.LivingStationCargoStaff,
        CCVars.LivingStationEngineeringStaff,
        CCVars.LivingStationMedicalStaff,
        CCVars.LivingStationSecurityStaff,
        CCVars.LivingStationScienceStaff,
    });
}
