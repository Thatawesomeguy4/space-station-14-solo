using System.Globalization;
using System.Linq;
using Content.Shared.CCVar;
using Robust.Client.Console;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Configuration;

namespace Content.Client.Lobby.UI;

public sealed partial class NpcStaffingWindow : DefaultWindow
{
    private readonly IConfigurationManager _config;
    private readonly Label[] _activeCounts = new Label[LivingStationStaffing.Departments.Count];
    private readonly Label[] _shortfalls = new Label[LivingStationStaffing.Departments.Count];

    public NpcStaffingWindow(IConfigurationManager config, IClientConsoleHost console)
    {
        _config = config;
        Title = Loc.GetString("living-station-staffing-title");
        var labels = new[]
        {
            "living-station-staffing-service",
            "living-station-staffing-cargo",
            "living-station-staffing-engineering",
            "living-station-staffing-medical",
            "living-station-staffing-security",
            "living-station-staffing-science",
        };
        var departments = LivingStationStaffing.Departments;
        var rows = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        Contents.AddChild(rows);
        var table = new GridContainer { Columns = 4, HSeparationOverride = 12, VSeparationOverride = 4 };
        rows.AddChild(table);
        table.AddChild(new Label { Text = Loc.GetString("living-station-staffing-department") });
        table.AddChild(new Label { Text = Loc.GetString("living-station-staffing-active") });
        table.AddChild(new Label { Text = Loc.GetString("living-station-staffing-target") });
        table.AddChild(new Label { Text = Loc.GetString("living-station-staffing-shortfall") });

        var inputs = new SpinBox[departments.Count];
        for (var i = 0; i < departments.Count; i++)
        {
            table.AddChild(new Label { Text = Loc.GetString(labels[i]) });
            table.AddChild(_activeCounts[i] = new Label());
            var input = new SpinBox { IsValid = value => value is >= 0 and <= CCVars.LivingStationMaxDepartmentStaff };
            input.InitDefaultButtons();
            input.OverrideValue(Math.Clamp(config.GetCVar(departments[i]), 0, CCVars.LivingStationMaxDepartmentStaff));
            table.AddChild(inputs[i] = input);
            table.AddChild(_shortfalls[i] = new Label());
        }

        var description = new RichTextLabel { MaxWidth = 330 };
        description.SetMessage(Loc.GetString("living-station-staffing-next-round"));
        rows.AddChild(description);
        var save = new Button { Text = Loc.GetString("living-station-staffing-apply") };
        save.OnPressed += _ =>
        {
            var counts = string.Join(" ", inputs.Select(input => input.Value.ToString(CultureInfo.InvariantCulture)));
            console.ExecuteCommand($"{LivingStationStaffing.Command} {counts}");
            Close();
        };
        rows.AddChild(save);
    }

    protected override void EnteredTree()
    {
        base.EnteredTree();
        _config.OnValueChanged(CCVars.LivingStationStaffingStatus, RefreshStatus, true);
    }

    protected override void ExitedTree()
    {
        _config.UnsubValueChanged(CCVars.LivingStationStaffingStatus, RefreshStatus);
        base.ExitedTree();
    }

    private void RefreshStatus(string status)
    {
        var groups = status.Split('|');
        var active = groups.Length == 3 ? groups[0].Split(',') : [];
        var spawned = groups.Length == 3 ? groups[1].Split(',') : [];
        var requested = groups.Length == 3 ? groups[2].Split(',') : [];

        for (var i = 0; i < _activeCounts.Length; i++)
        {
            _activeCounts[i].Text = i < active.Length ? active[i] : "–";
            _shortfalls[i].Text = i < spawned.Length && i < requested.Length &&
                int.TryParse(spawned[i], out var placed) && int.TryParse(requested[i], out var target)
                ? Math.Max(0, target - placed).ToString(CultureInfo.InvariantCulture)
                : "–";
        }
    }
}
