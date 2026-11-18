using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Spawn Living Station crew and start supported work on maps with the required markers.
    /// </summary>
    public static readonly CVarDef<bool> LivingStationEnabled =
        CVarDef.Create("living_station.enabled", false, CVar.SERVERONLY);

    public const int LivingStationMaxDepartmentStaff = 6;
    private const CVar StaffingFlags = CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE;

    public static readonly CVarDef<int> LivingStationServiceStaff = CVarDef.Create("living_station.service_staff", 3, StaffingFlags);
    public static readonly CVarDef<int> LivingStationCargoStaff = CVarDef.Create("living_station.cargo_staff", 1, StaffingFlags);
    public static readonly CVarDef<int> LivingStationEngineeringStaff = CVarDef.Create("living_station.engineering_staff", 1, StaffingFlags);
    public static readonly CVarDef<int> LivingStationMedicalStaff = CVarDef.Create("living_station.medical_staff", 1, StaffingFlags);
    public static readonly CVarDef<int> LivingStationSecurityStaff = CVarDef.Create("living_station.security_staff", 1, StaffingFlags);
    public static readonly CVarDef<int> LivingStationScienceStaff = CVarDef.Create("living_station.science_staff", 1, StaffingFlags);

    public static readonly CVarDef<string> LivingStationStaffingStatus =
        CVarDef.Create("living_station.staffing_status", "", CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<bool> LivingStationStaffingAvailable =
        CVarDef.Create("living_station.staffing_available", false, CVar.SERVER | CVar.REPLICATED);

    /// <summary>Schedule Living Station maintenance and social incidents on Saltern.</summary>
    public static readonly CVarDef<bool> LivingStationDirectorEnabled =
        CVarDef.Create("living_station.director_enabled", false, CVar.SERVERONLY);

    /// <summary>Story Director pacing profile: quiet, normal, or chaotic.</summary>
    public static readonly CVarDef<string> LivingStationDirectorPacing =
        CVarDef.Create("living_station.director_pacing", "normal", CVar.SERVERONLY);

    /// <summary>Allow the Living Station bartender and Chief Engineer to answer nearby speech through local Ollama.</summary>
    public static readonly CVarDef<bool> LivingStationOllamaEnabled =
        CVarDef.Create("living_station.ollama_enabled", false, CVar.SERVERONLY);

    /// <summary>Allow brief crew conversations while Ollama dialogue is enabled.</summary>
    public static readonly CVarDef<bool> LivingStationNpcConversationsEnabled =
        CVarDef.Create("living_station.npc_conversations_enabled", true, CVar.SERVERONLY);

    public static readonly CVarDef<string> LivingStationOllamaModel =
        CVarDef.Create("living_station.ollama_model", "qwen2.5:3b-instruct", CVar.SERVERONLY);
}
