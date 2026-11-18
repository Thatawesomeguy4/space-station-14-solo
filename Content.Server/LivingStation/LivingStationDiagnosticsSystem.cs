using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Server.GameTicking;
using Content.Server.NPC.Components;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.LivingStation;

/// <summary>On-demand snapshots. Reading the work board never dispatches work or changes timers.</summary>
public sealed partial class LivingStationDiagnosticsSystem : EntitySystem
{
    [Dependency] private LivingStationSystem _station = default!;
    [Dependency] private LivingStationWorkSystem _work = default!;
    [Dependency] private LivingStationIncidentSystem _incidents = default!;
    [Dependency] private LivingStationCargoSystem _cargo = default!;
    [Dependency] private LivingStationEngineeringSystem _engineering = default!;
    [Dependency] private LivingStationBartenderDialogueSystem _dialogue = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ServerGameTicker _ticker = default!;

    internal string[] ReadReport()
    {
        var lines = new List<string>
        {
            $"Living Station work board — round {_ticker.RoundId}, {_ticker.RunLevel}, simulation {Seconds(_timing.CurTime)}s",
            $"Enabled={_config.GetCVar(CCVars.LivingStationEnabled)}, director={_config.GetCVar(CCVars.LivingStationDirectorEnabled)}, " +
            $"pacing={_config.GetCVar(CCVars.LivingStationDirectorPacing)}, Ollama={_config.GetCVar(CCVars.LivingStationOllamaEnabled)}",
            $"Staffing active|spawned|requested (Service,Cargo,Engineering,Medical,Security,Science): {_config.GetCVar(CCVars.LivingStationStaffingStatus)}",
            $"Round claims: assigned={_work.Assigned}, released={_work.Released}, busy-rejections={_work.RejectedBusy}, reserved-rejections={_work.RejectedReserved}",
            $"Recorded order outcomes: successful={_work.Completed}, unsuccessful={_work.Failed}. Releases alone do not count as success; janitor HTN work is outside these counters.",
        };
        var inference = _dialogue.Transport.Counters;
        lines.Add($"Inference (transport lifetime): started={inference.Started}, finished={inference.Finished}, failed={inference.Failed}, " +
                  $"in-flight={inference.Started - inference.Finished}, total-wall-ms={inference.ElapsedMilliseconds}");

        var moving = 0;
        var noPath = 0;
        var active = 0;
        foreach (var (worker, department) in _station.Crew.OrderBy(entry => entry.Value).ThenBy(entry => entry.Key))
        {
            if (TerminatingOrDeleted(worker))
                continue;

            var state = HasComp<ActorComponent>(worker) ? "player controlled" : !_mobs.IsAlive(worker) ? "incapacitated/dead" : "autonomous";
            var facts = (CrewConversationFacts) _incidents.ReadCrewConversationContext(worker, department).Facts;
            var hasClaim = TryComp<LivingStationWorkClaimComponent>(worker, out var claim);
            if (hasClaim)
                active++;
            var route = "none";
            if (TryComp<NPCSteeringComponent>(worker, out var steering))
            {
                route = $"{steering.Status} → {steering.Coordinates}";
                if (steering.Status == SteeringStatus.Moving)
                    moving++;
                if (steering.Status == SteeringStatus.NoPath)
                    noPath++;
            }

            TryComp<LivingStationWorkHistoryComponent>(worker, out var history);
            lines.Add($"{department}: {Name(worker)} ({worker}) [{state}] stage={facts.WorkStage ?? facts.Routine ?? "routine/available"}; " +
                      $"target={(hasClaim ? claim!.Target.ToString() : facts.WorkTarget ?? "none")}; requester={Describe(claim?.Requester)}; " +
                      $"age={(hasClaim ? Seconds(_timing.CurTime - claim!.Started) : "-")}s; " +
                      $"remaining={(hasClaim ? Seconds(claim!.Deadline - _timing.CurTime) : "-")}s; route={route}");
            lines.Add($"  Last outcome: {history?.LastOutcome ?? "none recorded"}; last unsuccessful outcome: {history?.LastFailure ?? "none recorded"}");
        }

        lines.Add($"Current gauges: active-claims={active}, moving-crew={moving}, no-path-crew={noPath}. Route gauges are snapshots, not cumulative failures.");
        lines.Add("Queued department work (dependencies are described by existing queues; shared request links arrive in P1):");
        lines.AddRange(_cargo.ReadQueuedWork());
        lines.AddRange(_engineering.ReadQueuedWork());
        return lines.ToArray();
    }

    private string Describe(EntityUid? uid) => uid is { } entity && !TerminatingOrDeleted(entity) ? $"{Name(entity)} ({entity})" : "none/unavailable";
    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
}

[AdminCommand(AdminFlags.Server)]
public sealed partial class LivingStationWorkCommand : LocalizedEntityCommands
{
    [Dependency] private LivingStationDiagnosticsSystem _diagnostics = default!;
    public override string Command => "livingstationwork";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }
        foreach (var line in _diagnostics.ReadReport())
            shell.WriteLine(line);
    }
}
