using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Spawn Living Station crew and start supported work on maps with the required markers.
    /// </summary>
    public static readonly CVarDef<bool> LivingStationEnabled =
        CVarDef.Create("living_station.enabled", false, CVar.SERVERONLY);

    /// <summary>Schedule Living Station maintenance and social incidents on Saltern.</summary>
    public static readonly CVarDef<bool> LivingStationDirectorEnabled =
        CVarDef.Create("living_station.director_enabled", false, CVar.SERVERONLY);

    /// <summary>Story Director pacing profile: quiet, normal, or chaotic.</summary>
    public static readonly CVarDef<string> LivingStationDirectorPacing =
        CVarDef.Create("living_station.director_pacing", "normal", CVar.SERVERONLY);

    /// <summary>Allow the Living Station bartender to answer nearby speech through local Ollama.</summary>
    public static readonly CVarDef<bool> LivingStationOllamaEnabled =
        CVarDef.Create("living_station.ollama_enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<string> LivingStationOllamaModel =
        CVarDef.Create("living_station.ollama_model", "qwen2.5:3b-instruct", CVar.SERVERONLY);
}
