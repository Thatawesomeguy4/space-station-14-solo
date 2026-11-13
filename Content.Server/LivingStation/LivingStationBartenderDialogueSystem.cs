using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Shared.ActionBlocker;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.GameTicking;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Microsoft.Data.Sqlite;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server.LivingStation;

public sealed partial class LivingStationBartenderDialogueSystem : EntitySystem
{
    private const string Address = "bartender";
    private const float ConversationRange = 4f;
    private const int HistoryLength = 3;
    private const int CustomerLimit = 16;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(2);

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private ServerGameTicker _ticker = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IResourceManager _resources = default!;

    private readonly Dictionary<(EntityUid Mob, NetUserId User), Queue<BartenderExchange>> _history = new();
    internal OllamaBartenderClient Transport { get; set; } = new();
    internal LivingStationMemoryStore? Memory { get; set; }
    private PendingReply? _pending;
    private TimeSpan _nextRequest;

    private sealed record PendingReply(EntityUid Bartender, EntityUid Customer, NetUserId User, string Message, int Round,
        long Started, CancellationTokenSource Cancellation, Task<BartenderReply> Reply);

    private bool Enabled => _config.GetCVar(CCVars.LivingStationEnabled) && _config.GetCVar(CCVars.LivingStationOllamaEnabled);

    public override void Initialize()
    {
        SubscribeLocalEvent<ActorComponent, EntitySpokeEvent>(OnCustomerSpoke);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        Subs.CVar(_config, CCVars.LivingStationEnabled, OnSettingChanged);
        Subs.CVar(_config, CCVars.LivingStationOllamaEnabled, OnSettingChanged);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (_preferences.TryGetSavedProfileId(ev.Player.UserId, ev.Profile, out var profileId))
            EnsureComp<BartenderCustomerComponent>(ev.Mob).Customer = new(ev.Player.UserId, profileId);
    }

    public override void Shutdown()
    {
        Reset();
        Transport.Dispose();
        base.Shutdown();
    }

    private void OnCustomerSpoke(EntityUid customer, ActorComponent actor, EntitySpokeEvent ev)
    {
        if (!Enabled || _ticker.RunLevel != GameRunLevel.InRound || _pending != null || _timing.CurTime < _nextRequest ||
            ev.Channel != null || ev.ObfuscatedMessage != null || !TryGetAddressedMessage(ev.Message, Address, out var message))
            return;

        var counters = EntityQueryEnumerator<BarServiceCounterComponent>();
        while (counters.MoveNext(out var counter))
        {
            if (counter.Bartender is not { } bartender || !CanConverse(bartender, customer))
                continue;

            var model = _config.GetCVar(CCVars.LivingStationOllamaModel);
            var user = actor.PlayerSession.UserId;
            var history = _history.TryGetValue((customer, user), out var previous) ? previous.ToArray() : [];
            BartenderCustomer? identity = null;
            if (TryComp<BartenderCustomerComponent>(customer, out var binding) && binding.Customer.UserId == user)
                identity = binding.Customer;

            if (identity != null && Memory == null && _resources.UserData.RootDir is { } root)
                Memory = new LivingStationMemoryStore(Path.Combine(root, LivingStationMemoryStore.FileName));

            var started = Stopwatch.GetTimestamp();
            var cancellation = new CancellationTokenSource(OllamaBartenderClient.Deadline);
            var token = cancellation.Token;
            var transport = Transport;
            var memory = Memory;
            var reply = Task.Run(() => ReplyAsync(transport, memory, identity, model, history, message, token));
            _pending = new(bartender, customer, user, message, _ticker.RoundId, started, cancellation, reply);
            _nextRequest = _timing.CurTime + Cooldown;
            return;
        }
    }

    private static async Task<BartenderReply> ReplyAsync(OllamaBartenderClient transport, LivingStationMemoryStore? memory,
        BartenderCustomer? identity, string model, BartenderExchange[] history, string message, CancellationToken cancellation)
    {
        string? reportedShipName = null;
        if (memory != null && identity is { } customer)
        {
            var nameToSave = GetReportedShipName(message);
            var recall = message.Trim().TrimEnd('?').Equals("what did I say my ship was called", StringComparison.OrdinalIgnoreCase);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (nameToSave != null)
                {
                    memory.SaveShipName(customer, nameToSave, cancellation);
                    return new(BartenderReplyKind.NoteSaved, nameToSave);
                }

                reportedShipName = memory.ReadShipName(customer);
                if (recall)
                    return reportedShipName == null ? new(BartenderReplyKind.NoteMissing) : new(BartenderReplyKind.NoteRecalled, reportedShipName);
            }
            catch (SqliteException)
            {
                if (nameToSave != null || recall)
                    return new(BartenderReplyKind.NoteUnavailable);
            }
        }

        return await transport.AskAsync(model, history, message, reportedShipName, cancellation).ConfigureAwait(false);
    }

    private static string? GetReportedShipName(string message)
    {
        const string prefix = "my ship is called ";
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = message.AsSpan(prefix.Length).Trim().TrimEnd('.').TrimEnd();
        if (name.IsEmpty || name.Length > 64)
            return null;

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character) && character is not (' ' or '-' or '\''))
                return null;
        }

        return name.ToString();
    }

    public override void Update(float frameTime)
    {
        if (_pending is not { } pending || !pending.Reply.IsCompleted)
            return;

        _pending = null;
        pending.Cancellation.Dispose();
        var reply = pending.Reply.IsCompletedSuccessfully ? pending.Reply.GetAwaiter().GetResult() : new(BartenderReplyKind.Unavailable);
        if (!Enabled || _ticker.RunLevel != GameRunLevel.InRound || pending.Round != _ticker.RoundId ||
            !TryComp<ActorComponent>(pending.Customer, out var actor) || actor.PlayerSession.UserId != pending.User ||
            !CanConverse(pending.Bartender, pending.Customer))
            return;

        if (Stopwatch.GetElapsedTime(pending.Started) >= OllamaBartenderClient.Deadline)
            reply = new(BartenderReplyKind.TimedOut);

        Log.Debug($"Living Station bartender reply {reply.Kind} in round {pending.Round}.");
        if (reply.Kind == BartenderReplyKind.NoAction)
            return;

        var speech = reply.Kind switch
        {
            BartenderReplyKind.NoteSaved => Loc.GetString("living-station-bar-memory-saved", ("name", reply.Speech!)),
            BartenderReplyKind.NoteRecalled => Loc.GetString("living-station-bar-memory-recalled", ("name", reply.Speech!)),
            BartenderReplyKind.NoteMissing => Loc.GetString("living-station-bar-memory-missing"),
            BartenderReplyKind.NoteUnavailable => Loc.GetString("living-station-bar-memory-unavailable"),
            _ => reply.Speech ?? Loc.GetString("living-station-bar-dialogue-fallback"),
        };
        _chat.TrySendInGameICMessage(pending.Bartender, speech, InGameICChatType.Speak, ChatTransmitRange.Normal,
            hideLog: true, checkRadioPrefix: false);
        Remember((pending.Customer, pending.User), new(pending.Message, speech));
    }

    private bool CanConverse(EntityUid bartender, EntityUid customer) =>
        bartender != customer && !HasComp<ActorComponent>(bartender) && HasComp<ActorComponent>(customer) &&
        _mobState.IsAlive(bartender) && _mobState.IsAlive(customer) && _blocker.CanSpeak(bartender) &&
        _interaction.InRangeUnobstructed(bartender, customer, ConversationRange);

    internal static bool TryGetAddressedMessage(string input, string address, out string message)
    {
        message = string.Empty;
        var text = input.AsSpan().TrimStart();
        if (!text.StartsWith(address, StringComparison.OrdinalIgnoreCase) || text.Length <= address.Length ||
            text[address.Length] is not (' ' or ',' or ':' or '!'))
            return false;

        text = text[address.Length..].TrimStart(" ,:!");
        if (text.IsEmpty)
            return false;

        message = text[..Math.Min(text.Length, OllamaBartenderClient.TextLimit)].ToString();
        return true;
    }

    private void Remember((EntityUid Mob, NetUserId User) customer, BartenderExchange exchange)
    {
        if (!_history.TryGetValue(customer, out var history))
        {
            if (_history.Count >= CustomerLimit)
                return;
            history = new Queue<BartenderExchange>(HistoryLength);
            _history.Add(customer, history);
        }

        if (history.Count == HistoryLength)
            history.Dequeue();
        history.Enqueue(exchange);
    }

    private void OnSettingChanged(bool enabled)
    {
        if (!enabled)
            Reset();
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        if (ev.New != GameRunLevel.InRound)
            Reset();
    }

    private void Reset()
    {
        if (_pending is { } pending)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
            _pending = null;
        }

        _history.Clear();
        _nextRequest = TimeSpan.Zero;
    }
}

[RegisterComponent]
internal sealed partial class BartenderCustomerComponent : Component
{
    internal BartenderCustomer Customer;
}
