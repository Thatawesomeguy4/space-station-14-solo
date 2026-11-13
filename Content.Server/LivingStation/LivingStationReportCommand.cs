using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.LivingStation;

[AdminCommand(AdminFlags.Debug)]
public sealed partial class LivingStationReportCommand : LocalizedEntityCommands
{
    [Dependency] private LivingStationSecuritySystem _security = default!;

    public override string Command => "livingstationreport";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0 || shell.Player?.AttachedEntity is not { } reporter)
        {
            shell.WriteError(Help);
            return;
        }

        if (_security.TryReport(reporter))
            shell.WriteLine(Loc.GetString("living-station-report-accepted"));
        else
            shell.WriteError(Loc.GetString("living-station-report-unavailable"));
    }
}
