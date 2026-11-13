using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Content.Client.UserInterface.Systems.Chat;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.LivingStation;
using Content.Server.Preferences.Managers;
using Content.Server.Radio.EntitySystems;
using Content.Shared.AlertLevel;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Light.Components;
using Content.Shared.Preferences;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Station.Systems;
using Content.Shared.Verbs;
using Robust.Client.UserInterface;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.NPC;

[TestFixture]
public sealed class BartenderDialogueTest : GameTest
{
    [SidedDependency(Side.Client)] private readonly IUserInterfaceManager _uiManager = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
        DummyTicker = false,
        Map = PoolManager.TestStation,
    };

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task RepliesRememberConversationAndLeaveServiceAvailable()
    {
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var chat = Server.System<ChatSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var verbs = Server.System<SharedVerbSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var replies = new ConcurrentQueue<string>();
        var handler = new DialogueHandler();
        using var fakeTransport = new OllamaBartenderClient(handler);
        var originalTransport = dialogue.Transport;
        EntityUid counter = default;
        EntityUid bartender = default;
        EntityUid customer = default;
        EntityUid water = default;
        NetEntity bartenderNet = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);

        try
        {
            await Server.WaitAssertion(() =>
            {
                dialogue.Transport = fakeTransport;
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                ticker.RestartRound();

                var counters = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent>();
                Assert.That(counters.MoveNext(out counter, out var bar), Is.True);
                bartender = bar!.Bartender!.Value;
                bartenderNet = SEntMan.GetNetEntity(bartender);
                customer = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(counter).Coordinates.Offset(-bar.StaffOffset));
                Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);

                Say("Bartender, my ship is called Blue Comet.");
            });

            await WaitForReply(1);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[0], Does.Contain("Blue Comet"));
                Say("Bartender, what did I say my ship was called?");
            });

            await WaitForReply(2);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[1], Does.Contain("Blue Comet"));
                Say("Bartender, give me a moment.");
            });

            await WaitForReply(3);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[2], Does.Contain("request water"));
                Say("Bartender, are you open?");
            });

            await WaitForReply(4);
            await Pair.RunTicksSync(65);
            await Server.WaitAssertion(() =>
            {
                Assert.That(replies.ToArray()[3], Does.Contain("request water"));
                Say("Bartender, are you there?");
            });

            await handler.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Server.WaitAssertion(() =>
            {
                var request = verbs.GetLocalVerbs(counter, customer, typeof(Verb)).Single(verb => verb.Text == "Request water");
                verbs.ExecuteVerb(request, customer, counter);
                water = SComp<BarServiceOrderComponent>(bartender).Drink;
            });
            await PoolManager.WaitUntil(Server, () => !SEntMan.HasComponent<BarServiceOrderComponent>(bartender));
            await Server.WaitAssertion(() =>
            {
                Assert.That(transform.GetGridTilePositionOrDefault(water), Is.EqualTo(transform.GetGridTilePositionOrDefault(counter)));
                Assert.That(replies, Has.Count.EqualTo(4));
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
            });
            await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Pair.RunTicksSync(2);
            Assert.That(replies, Has.Count.EqualTo(4));
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Transport = originalTransport;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
        }

        void Say(string message) => chat.TrySendInGameICMessage(customer, message, InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true);

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == bartenderNet)
                replies.Enqueue(message.Message);
        }

        async Task WaitForReply(int count)
        {
            for (var i = 0; i < 300 && replies.Count < count; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies, Has.Count.EqualTo(count));
        }
    }

    [Test]
    public async Task ShipNoteSurvivesRoundRestartAndFollowsTheSpawnedCharacter()
    {
        const string recall = "Bartender, what did I say my ship was called?";
        var dialogue = Server.System<LivingStationBartenderDialogueSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var chat = Server.System<ChatSystem>();
        var transform = Server.System<SharedTransformSystem>();
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var user = ServerSession.UserId;
        var replies = new ConcurrentQueue<string>();
        var folder = Directory.CreateTempSubdirectory("ss14-bartender-");
        var path = Path.Combine(folder.FullName, LivingStationMemoryStore.FileName);
        var originalMemory = dialogue.Memory;
        EntityUid customer = default;
        NetEntity bartenderNet = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                var firstSlot = preferences.GetPreferences(user).SelectedCharacterIndex;
                var secondSlot = firstSlot == 0 ? 1 : 0;
                // Keep the test phrases intact across randomized integration-test profiles.
                var first = HumanoidCharacterProfile.DefaultWithSpecies("Human");
                preferences.SetProfile(user, firstSlot, first).GetAwaiter().GetResult();
                dialogue.Memory = new LivingStationMemoryStore(path);
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, true);
                RestartAndJoin();
                Say("Bartender, my ship is called Blue Comet.");

                // Two profiles can have the same name. Changing the lobby selection also leaves the
                // character already at the counter bound to its original profile.
                preferences.SetProfile(user, secondSlot, first.WithName(first.Name)).GetAwaiter().GetResult();
            });
            await AssertReply("I'll remember you said your ship is called", "Blue Comet");

            await Server.WaitAssertion(() => Say(recall));
            await AssertReply("You told me your ship is called", "Blue Comet");

            await Server.WaitAssertion(() =>
            {
                dialogue.Memory = new LivingStationMemoryStore(path);
                RestartAndJoin();
                Say(recall);
            });
            await AssertReply("I don't remember you telling me your ship's name.");

            await Server.WaitAssertion(() => Say("Bartender, my ship is called Red Dwarf."));
            await AssertReply("I'll remember you said your ship is called", "Red Dwarf");

            await Server.WaitAssertion(() =>
            {
                var prefs = preferences.GetPreferences(user);
                var otherSlot = prefs.SelectedCharacterIndex == 0 ? 1 : 0;
                // Saving a rename updates the existing profile row and preserves its note.
                preferences.SetProfile(user, otherSlot, prefs.Characters[otherSlot].WithName("Pat Morgan")).GetAwaiter().GetResult();
                dialogue.Memory = new LivingStationMemoryStore(path);
                RestartAndJoin();
                Say(recall);
            });
            await AssertReply("You told me your ship is called", "Blue Comet");
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationOllamaEnabled, false);
                dialogue.Memory = originalMemory;
            });
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
            File.Delete(path);
            folder.Delete();
        }

        void RestartAndJoin()
        {
            ticker.RestartRound();
            var counters = SEntMan.EntityQueryEnumerator<BarServiceCounterComponent>();
            Assert.That(counters.MoveNext(out var counter, out var bar), Is.True);
            bartenderNet = SEntMan.GetNetEntity(bar!.Bartender!.Value);

            ticker.MakeJoinGame(ServerSession, EntityUid.Invalid, "Passenger", silent: true);
            customer = ServerSession.AttachedEntity!.Value;
            Assert.That(SEntMan.HasComponent<BartenderCustomerComponent>(customer), Is.True);
            transform.SetCoordinates(customer, SComp<TransformComponent>(counter).Coordinates.Offset(-bar.StaffOffset));
        }

        void Say(string message) => chat.TrySendInGameICMessage(customer, message, InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true);

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local && message.SenderEntity == bartenderNet)
                replies.Enqueue(message.Message);
        }

        async Task AssertReply(params string[] expected)
        {
            for (var i = 0; i < 300 && replies.IsEmpty; i++)
                await Pair.RunTicksSync(2);

            Assert.That(replies.TryDequeue(out var reply), Is.True, $"Expected bartender speech containing {string.Join(", ", expected)}.");
            foreach (var text in expected)
                Assert.That(reply, Does.Contain(text));
            await Pair.RunTicksSync(65);
        }
    }

    [TestCase("quiet", 12)]
    [TestCase("chaotic", 5)]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task DirectorPacingControlsIncidentStarts(string profile, int minutesBetweenStarts)
    {
        var director = Server.System<LivingStationStoryDirectorSystem>();
        var engineering = Server.System<LivingStationEngineeringSystem>();
        var ticker = Server.System<ServerGameTicker>();
        var timing = Server.ResolveDependency<IGameTiming>();
        var interval = TimeSpan.FromMinutes(minutesBetweenStarts);
        TimeSpan firstStart = default;
        EntityUid fixture = default;

        await Server.WaitAssertion(() =>
        {
            Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
            Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, true);
            Server.CfgMan.SetCVar(CCVars.LivingStationDirectorPacing, profile);
            ticker.RestartRound();
            fixture = engineering.Fixture!.Value;
            firstStart = timing.CurTime + LivingStationStoryDirectorSystem.Warmup;
            director.Advance(firstStart);
            Assert.That(director.MaintenanceActive, Is.True);
        });

        await PoolManager.WaitUntil(Server, () => SComp<PoweredLightComponent>(fixture).CurrentLit,
            maxTicks: 800, tickStep: 10);

        await Server.WaitAssertion(() =>
        {
            var rumorStart = firstStart + interval;
            director.Advance(rumorStart - TimeSpan.FromSeconds(1));
            Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            director.Advance(rumorStart);
            Assert.That(director.RumorActive, Is.True);

            if (profile == "quiet")
            {
                director.Advance(rumorStart + TimeSpan.FromMinutes(2));
                Assert.That(director.RumorActive, Is.False);
                director.Advance(rumorStart + interval - TimeSpan.FromSeconds(1));
                Assert.That(director.MaintenanceActive, Is.False);
                director.Advance(rumorStart + interval);
                Assert.That(director.MaintenanceActive, Is.True);
                return;
            }

            var nextRepair = rumorStart + interval;
            var station = Server.System<StationSystem>().GetOwningStation(fixture)!.Value;
            var alerts = Server.System<AlertLevelSystem>();
            alerts.SetLevel(station, "Red", playSound: false, announce: false, force: true);
            director.Advance(nextRepair);
            Assert.That(director.MaintenanceActive, Is.False);
            Assert.That(director.RumorActive, Is.True);
            alerts.SetLevel(station, "Green", playSound: false, announce: false, force: true);

            nextRepair += TimeSpan.FromSeconds(30);
            director.Advance(nextRepair);
            Assert.That(director.MaintenanceActive, Is.True);
            Assert.That(director.RumorActive, Is.True);
            director.Advance(nextRepair + LivingStationEngineeringSystem.RepairTimeout);
            Assert.That(director.MaintenanceActive, Is.False);
            Assert.That(director.RumorActive, Is.True);
            Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineering.ChiefEngineer!.Value), Is.False);
            director.Advance(rumorStart + TimeSpan.FromMinutes(6));
            Assert.That(director.RumorActive, Is.False);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.PathfindingEnabled), true)]
    public async Task CommandStaffRememberTheLightWithDifferentAttribution()
    {
        var incidents = Server.System<LivingStationIncidentSystem>();
        var director = Server.System<LivingStationStoryDirectorSystem>();
        var timing = Server.ResolveDependency<IGameTiming>();
        var ticker = Server.System<ServerGameTicker>();
        var transform = Server.System<SharedTransformSystem>();
        var chat = Server.System<ChatSystem>();
        var radio = Server.System<RadioSystem>();
        var replies = new ConcurrentQueue<ChatMessage>();
        var folder = Directory.CreateTempSubdirectory("ss14-incident-");
        var path = Path.Combine(folder.FullName, LivingStationMemoryStore.FileName);
        EntityUid engineer = default;
        EntityUid security = default;
        EntityUid fixture = default;
        EntityUid customer = default;
        TimeSpan incidentStart = default;

        await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded += OnChat);
        try
        {
            await Server.WaitAssertion(() =>
            {
                incidents.Memory = new LivingStationMemoryStore(path);
                Server.CfgMan.SetCVar(CCVars.LivingStationEnabled, true);
                Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, true);
                Restart();
                var headset = SComp<WearingHeadsetComponent>(engineer).Headset;
                radio.SendRadioMessage(customer, Loc.GetString("living-station-incident-radio"),
                    SProtoMan.Index<RadioChannelPrototype>("Command"), headset);
                Assert.That(incidents.Notes.ReportedAt, Is.Null);
            });
            await PoolManager.WaitUntil(Server, () => SComp<PoweredLightComponent>(fixture).CurrentLit, maxTicks: 800, tickStep: 10);

            Task writes = Task.CompletedTask;
            LightIncidentNotes saved = default;
            await Server.WaitAssertion(() =>
            {
                writes = incidents.Writes;
                saved = incidents.Notes;
                Assert.That(saved.ObservedAt, Is.Not.Null, "The Chief Engineer should observe the failed bulb.");
                foreach (var npc in new[] { engineer, security })
                {
                    Assert.That(SEntMan.TryGetComponent<WearingHeadsetComponent>(npc, out var worn), Is.True, $"Headset on {npc}");
                    Assert.That(SComp<EncryptionKeyHolderComponent>(worn!.Headset).Channels.Any(channel => channel.Id == "Command"), Is.True,
                        $"Command channel on {npc}");
                }
                Assert.That(saved.ObservedAt, Is.EqualTo(saved.ReportedAt));
                var rumorStart = incidentStart + LivingStationStoryDirectorSystem.StartInterval;
                director.Advance(rumorStart - TimeSpan.FromSeconds(1));
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
                director.Advance(rumorStart);
                Assert.That(director.RumorActive, Is.True);
            });
            await writes.WaitAsync(TimeSpan.FromSeconds(5));

            await Ask(security, "Head of Security, what have you heard?", "The Chief Engineer relayed an unconfirmed report");
            await Ask(engineer, "Chief Engineer, can you check the light?", "The fixture is lit now");
            await Ask(security, "Head of Security, what have you heard?", "The Chief Engineer checked the fixture");
            await Server.WaitAssertion(() =>
            {
                var rumorStart = incidentStart + LivingStationStoryDirectorSystem.StartInterval;
                director.Advance(rumorStart + TimeSpan.FromSeconds(1));
                Assert.That(director.RumorActive, Is.False);
                director.Advance(rumorStart + LivingStationStoryDirectorSystem.RumorLifetime);
            });
            await Ask(security, "Head of Security, what have you heard?", "I have no active report");

            await Server.WaitAssertion(() =>
            {
                incidents.Memory = new LivingStationMemoryStore(path);
                Restart(checkAlert: true);
                // Leave the new round's fixture broken so all recalled notes must come from disk.
                SEntMan.RemoveComponent<EngineeringRepairComponent>(engineer);
                Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.False);
            });
            await PoolManager.WaitUntil(Server, () => incidents.Notes.ReportedAt != null);
            await Server.WaitAssertion(() => Assert.That(incidents.Notes, Is.EqualTo(saved)));

            await Ask(engineer, "Chief Engineer, what happened?", "I saw a broken light");
            await Ask(security, "Head of Security, what happened?", "The Chief Engineer reported a broken light");
            await Ask(security, "Head of Security, who broke it?", "I don't know who or what broke it");
            await Ask(security, "Head of Security, is the light fixed?", "The fixture still needs a working bulb");
            await Server.WaitAssertion(() =>
            {
                Server.CfgMan.SetCVar(CCVars.LivingStationDirectorEnabled, false);
                director.Advance(incidentStart + LivingStationEngineeringSystem.RepairTimeout);
                Assert.That(director.MaintenanceActive, Is.False);
                Assert.That(SEntMan.HasComponent<LivingStationWorkClaimComponent>(engineer), Is.False);
                Assert.That(SComp<PoweredLightComponent>(fixture).CurrentLit, Is.False);
                director.Advance(incidentStart + LivingStationStoryDirectorSystem.StartInterval);
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            });
        }
        finally
        {
            await Server.WaitPost(() => incidents.Memory = null);
            await Client.WaitPost(() => _uiManager.GetUIController<ChatUIController>().MessageAdded -= OnChat);
            File.Delete(path);
            folder.Delete();
        }

        void Restart(bool checkAlert = false)
        {
            ticker.RestartRound();
            incidentStart = timing.CurTime + LivingStationStoryDirectorSystem.Warmup;
            var engineering = Server.System<LivingStationEngineeringSystem>();
            Assert.That(engineering.ChiefEngineer, Is.Not.Null);
            Assert.That(SEntMan.HasComponent<EngineeringRepairComponent>(engineering.ChiefEngineer!.Value), Is.False);
            director.Advance(incidentStart - TimeSpan.FromSeconds(1));
            Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
            if (checkAlert)
            {
                var station = Server.System<StationSystem>().GetOwningStation(engineering.Fixture!.Value)!.Value;
                var alerts = Server.System<AlertLevelSystem>();
                alerts.SetLevel(station, "Red", playSound: false, announce: false, force: true);
                director.Advance(incidentStart);
                Assert.That(director.MaintenanceActive || director.RumorActive, Is.False);
                Assert.That(SEntMan.HasComponent<EngineeringRepairComponent>(engineering.ChiefEngineer!.Value), Is.False);
                alerts.SetLevel(station, "Green", playSound: false, announce: false, force: true);
                incidentStart += TimeSpan.FromSeconds(30);
            }
            director.Advance(incidentStart);
            Assert.That(director.MaintenanceActive, Is.True);
            var repairs = SEntMan.EntityQueryEnumerator<EngineeringRepairComponent, LivingStationWorkClaimComponent>();
            Assert.That(repairs.MoveNext(out engineer, out _, out var claim), Is.True);
            fixture = claim!.Target.EntityId;
            var officers = SEntMan.EntityQueryEnumerator<SecurityPatrolComponent>();
            Assert.That(officers.MoveNext(out security, out _), Is.True);
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(engineer).EntityPrototype!.ID, Is.EqualTo("MobLivingStationChiefEngineer"));
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(security).EntityPrototype!.ID, Is.EqualTo("MobLivingStationHeadOfSecurity"));
            customer = SEntMan.SpawnAttachedTo("MobHuman", SComp<TransformComponent>(security).Coordinates);
            Assert.That(Server.PlayerMan.SetAttachedEntity(ServerSession, customer, force: true), Is.True);
        }

        async Task Ask(EntityUid speaker, string question, string expected)
        {
            NetEntity sender = default;
            await Server.WaitAssertion(() =>
            {
                sender = SEntMan.GetNetEntity(speaker);
                transform.SetCoordinates(customer, SComp<TransformComponent>(speaker).Coordinates);
                chat.TrySendInGameICMessage(customer, question, InGameICChatType.Speak, ChatTransmitRange.Normal,
                    hideLog: true, checkRadioPrefix: false);
            });
            await Pair.RunTicksSync(65);
            Assert.That(replies.Any(reply => reply.SenderEntity == sender && reply.Message.Contains(expected)), Is.True,
                $"Expected '{expected}', got: {string.Join(", ", replies.Where(reply => reply.SenderEntity == sender).Select(reply => reply.Message))}");
            replies.Clear();
        }

        void OnChat(ChatMessage message)
        {
            if (message.Channel == ChatChannel.Local)
                replies.Enqueue(message);
        }
    }

    private sealed class DialogueHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requests);
            if (count == 4)
                throw new HttpRequestException("Test endpoint unavailable");

            if (count == 5)
            {
                Stalled.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Cancelled.TrySetResult();
                }
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var userMessage = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var context = JsonDocument.Parse(userMessage);
            var history = context.RootElement.GetProperty("history");
            var content = count switch
            {
                1 => """{"version":1,"intent":"Speak","text":"I'll remember the Blue Comet."}""",
                2 when history.GetArrayLength() == 1 && history[0].GetProperty("Customer").GetString()!.Contains("Blue Comet") =>
                    """{"version":1,"intent":"Speak","text":"You said Blue Comet."}""",
                _ => """{"version":1,"intent":"RunCommand","text":"Invalid"}""",
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message = new { content } })),
            };
        }
    }
}
