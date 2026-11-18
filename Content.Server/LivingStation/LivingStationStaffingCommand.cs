using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Logs;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Robust.Shared.Configuration;
using Robust.Shared.Console;

namespace Content.Server.LivingStation;

[AdminCommand(AdminFlags.Server)]
public sealed partial class LivingStationStaffingCommand : LocalizedCommands
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IAdminLogManager _logs = default!;

    public override string Command => LivingStationStaffing.Command;

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var departments = LivingStationStaffing.Departments;
        // Older callers supply the five original departments and leave Science unchanged.
        if (args.Length != departments.Count && args.Length != departments.Count - 1)
        {
            shell.WriteError(Help);
            return;
        }

        var counts = new int[args.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out counts[i]) ||
                counts[i] is < 0 or > CCVars.LivingStationMaxDepartmentStaff)
            {
                shell.WriteError(Loc.GetString("living-station-staffing-invalid", ("max", CCVars.LivingStationMaxDepartmentStaff)));
                return;
            }
        }

        for (var i = 0; i < counts.Length; i++)
            _config.SetCVar(departments[i], counts[i]);

        _config.SaveToFile();
        var staffing = string.Join(", ", departments.Select(setting => $"{setting.Name}={_config.GetCVar(setting)}"));
        _logs.Add(LogType.AdminCommands, LogImpact.Medium,
            $"{shell.Player?.Name ?? "Server"} changed Living Station staffing: {staffing}");
        shell.WriteLine(Loc.GetString("living-station-staffing-saved"));
    }
}
