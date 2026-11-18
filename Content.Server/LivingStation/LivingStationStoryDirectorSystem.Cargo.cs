using System.Linq;
using Content.Server.Chat.Systems;
using Content.Server.Power.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Player;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationStoryDirectorSystem
{
    private sealed record CargoFault(EntityUid Processor, EntityUid Reporter, TimeSpan Expires, bool Reported = false);
    private CargoFault? _cargoFault;
    private bool _nextMaintenanceIsCargo;
    internal bool CargoFaultActive => _cargoFault != null;

    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private MobStateSystem _mobs = default!;
    [Dependency] private ChatSystem _chat = default!;

    internal bool TryStartCargoPowerFault(TimeSpan now)
    {
        if (_cargoFault != null)
            return false;

        var points = EntityQueryEnumerator<EngineeringServicePointComponent, CargoOreProcessorComponent, TransformComponent>();
        while (points.MoveNext(out var processor, out var point, out _, out var position))
        {
            if (!point.CargoPower || !position.Anchored || position.MapID != _ticker.DefaultMap || position.GridUid is not { } grid ||
                !_power.IsPowered(processor) || !_engineering.CanRespondToCargoFault(processor))
                continue;

            var routes = EntityQueryEnumerator<CargoDeliveryPickupComponent, TransformComponent>();
            EntityUid reporter = default;
            while (routes.MoveNext(out var route, out var transform))
            {
                if (transform.GridUid != grid)
                    continue;
                reporter = route.Workers.FirstOrDefault(worker => _mobs.IsAlive(worker) && !HasComp<ActorComponent>(worker));
                if (reporter.IsValid())
                    break;
            }

            if (!reporter.IsValid())
                continue;

            _cargoFault = new CargoFault(processor, reporter, now + TimeSpan.FromSeconds(90));
            _power.SetPowerDisabled(processor, true);
            return true;
        }

        return false;
    }

    private void UpdateCargoPowerFault(TimeSpan now)
    {
        if (_cargoFault is not { } fault)
            return;

        if (!fault.Reported && !TerminatingOrDeleted(fault.Processor) && !_power.IsPowered(fault.Processor))
        {
            SayCargo(fault.Reporter, "living-station-cargo-power-failed");
            _engineering.ReportCargoFault(fault.Processor, fault.Reporter);
            _cargoFault = fault with { Reported = true };
        }
        else if (fault.Reported && !TerminatingOrDeleted(fault.Processor) && _power.IsPowered(fault.Processor))
        {
            SayCargo(fault.Reporter, "living-station-cargo-power-restored");
            _cargoFault = null;
        }
        else if (now >= fault.Expires)
        {
            SayCargo(fault.Reporter, "living-station-cargo-power-waiting");
            _cargoFault = null;
        }
    }

    private void SayCargo(EntityUid worker, string message)
    {
        if (_mobs.IsAlive(worker) && !HasComp<ActorComponent>(worker))
            _chat.TrySendInGameICMessage(worker, Loc.GetString(message), InGameICChatType.Speak,
                ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false);
    }
}
