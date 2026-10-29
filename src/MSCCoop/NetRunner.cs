using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace MSCCoop;

sealed class NetRunner : MonoBehaviour
{
    const float StatusInterval = 0.25f;

    static bool launchOptionsApplied;

    string dumpDir;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly StringBuilder status = new();
    string modVersion;
    uint gameBuild;
    string playerName = "Player";
    SteamLobby lobby;
    SteamTransport steam;
    LanTransport lan;
    SimulatedTransport sim;
    Session session;
    GameFlow flow;
    readonly ObjectSync objects = new();
    PlayerSync players;
    PassengerSeat passenger;
    FsmSync fsms;
    readonly WorldSync world = new();
    readonly MoneySync money = new();
    readonly PhoneSync phone = new();
    SpawnSync spawns;
    readonly HoistSync hoist = new();
    SharedValueSync shared;
    CoopMenu menu;
    double simLatency, simJitter, simLoss;
    string statusText = "";
    readonly Stopwatch profile = new();
    int profiledFrames;
    double profiledMs;
    float nextStatus;
    bool overlay = true;

    double Now => clock.Elapsed.TotalSeconds;

    public bool Active => session != null && session.State != SessionState.Idle;
    public Session Session => session;
    public SteamLobby Lobby => lobby;
    public GameFlow Flow => flow;

    // The last thing that went wrong, shown in the co-op menu.
    public string Notice { get; private set; } = "";

    public void Init(string version, string dataDir)
    {
        modVersion = version;
        dumpDir = Path.Combine(dataDir, "dumps");
        var clientSaveDir = Path.Combine(dataDir, "client-save");
        passenger = new PassengerSeat(objects.Registry);
        players = new PlayerSync(objects.Registry, passenger);
        players.Init();
        fsms = new FsmSync(objects.Registry, objects);
        spawns = new SpawnSync(objects.Registry, fsms);
        shared = new SharedValueSync(objects.Registry);
        flow = new GameFlow(clientSaveDir, Path.Combine(dataDir, "save-backups"));
        flow.OnLevelLoaded();
        menu = new CoopMenu(this);
        gameBuild = ComputeGameBuild();
        try
        {
            playerName = SteamFriends.GetPersonaName();
            lobby = new SteamLobby(modVersion, gameBuild, playerName);
            lobby.Hosted += () => StartSession(steam, host: true, 0);
            lobby.JoinedAsClient += OnJoinedLobby;
            lobby.Failed += Report;
            lobby.Warned += Warn;
            lobby.MemberLeft += OnLobbyMemberLeft;
            steam = new SteamTransport(lobby.IsMember);
        }
        catch (Exception e)
        {
            ModConsole.Warning("Co-op: Steam is not available, only LAN sessions will work. " + e.Message);
        }

        // MOP switches off what is far from the player, which the other players still need in sync.
        if (ModLoader.IsModPresent("MOP"))
            Warn("MOP is installed. It switches off things far from you that the other players still use, turn it off for co-op.");

        // MSCLoader recreates every mod object on each return to the menu; launch options apply once.
        if (launchOptionsApplied)
            return;
        launchOptionsApplied = true;
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var id))
                JoinSteam(id);
            else if (args[i] == "-coop-lan-join" && IPAddress.TryParse(args[i + 1], out var address))
                JoinLan(address, LanTransport.DefaultPort);
        }
        if (Array.IndexOf(args, "-coop-lan-host") >= 0)
            HostLan(LanTransport.DefaultPort);
        flow.AutoStart = Array.IndexOf(args, "-coop-autostart") >= 0;
    }

    public void HostSteam(ELobbyType type, int maxPlayers)
    {
        if (!RequireSteam() || !RequireIdle())
            return;
        Notice = "";
        lobby.Create(type, maxPlayers);
    }

    public void JoinSteam(ulong lobbyId)
    {
        if (!RequireSteam() || !RequireIdle() || !flow.CanJoin())
            return;
        Notice = "";
        lobby.Join(lobbyId);
    }

    public void Invite()
    {
        if (RequireSteam())
            lobby.Invite();
    }

    public void HostLan(int port)
    {
        if (!RequireIdle())
            return;
        Notice = "";
        lan?.Dispose();
        lan = LanTransport.Listen(port);
        StartSession(lan, host: true, 0);
    }

    public void JoinLan(IPAddress address, int port)
    {
        if (!RequireIdle() || !flow.CanJoin())
            return;
        Notice = "";
        lan?.Dispose();
        lan = LanTransport.Connect(address, port);
        StartSession(lan, host: false, LanTransport.HostId);
    }

    public void Leave()
    {
        session?.Leave();
        lobby?.Leave();
    }

    public void SetSimulation(double latencyMs, double jitterMs, double lossPercent)
    {
        simLatency = latencyMs / 1000;
        simJitter = jitterMs / 1000;
        simLoss = lossPercent / 100;
        ApplySimulation();
    }

    public void ToggleOverlay() => overlay = !overlay;

    public void PrintVersion() =>
        ModConsole.Print("Co-op: mod " + modVersion + ", protocol " + Protocol.Version + ", game build " + gameBuild + ".");

    public void Dump() => ModConsole.Print("Co-op: state written to " + Diagnostics.Write(dumpDir, session, objects.Registry, shared));

    public void SetLogging(bool on)
    {
        Diagnostics.Logging = on;
        ModConsole.Print("Co-op: event log " + (on ? "on" : "off") + ".");
    }

    void StartSession(ITransport transport, bool host, ulong hostId)
    {
        sim = new SimulatedTransport(transport, Environment.TickCount);
        ApplySimulation();
        var config = new SessionConfig { ModVersion = modVersion, GameBuild = gameBuild, PlayerName = playerName };
        session = new Session(sim, config);
        flow.Attach(session, config);
        players.Attach(session);
        objects.Attach(session);
        fsms.Attach(session);
        world.Attach(session);
        money.Attach(session);
        phone.Attach(session);
        spawns.Attach(session);
        hoist.Attach(session);
        shared.Attach(session);
        session.PlayerJoined += p => ModConsole.Print("Co-op: " + p.Name + " joined");
        session.PlayerLeft += p => ModConsole.Print("Co-op: " + p.Name + " left");
        session.Ended += OnEnded;
        if (host)
            session.Host(Now);
        else
            session.Connect(hostId, Now);
        ModConsole.Print(host ? "Co-op: hosting" : "Co-op: connecting");
    }

    void OnJoinedLobby(ulong owner)
    {
        if (flow.CanJoin())
            StartSession(steam, host: false, owner);
        else
            lobby.Leave();
    }

    void OnEnded(EndReason reason)
    {
        flow.Detach();
        players.Detach();
        objects.Detach();
        fsms.Detach();
        world.Detach();
        money.Detach();
        phone.Detach();
        spawns.Detach();
        hoist.Detach();
        shared.Detach();
        var text = reason == EndReason.Rejected ? "rejected (" + session.LastRejectReason + ")" : reason.ToString();
        ModConsole.Print("Co-op: session ended, " + text);
        Notice = Describe(reason, session.LastRejectReason);
        lobby?.Leave();
    }

    static string Describe(EndReason reason, RejectReason reject) => reason switch
    {
        EndReason.Left => "",
        EndReason.HostLeft => "The host closed the session.",
        EndReason.TimedOut => "Lost the connection.",
        EndReason.ConnectFailed => "Could not reach the host.",
        _ => reject switch
        {
            RejectReason.Full => "The lobby is full.",
            RejectReason.InProgress => "The host is already in the game. Join while the host is in the main menu.",
            RejectReason.GameBuildMismatch => "The host has a different version of the game.",
            _ => "The host runs a different version of MSC Co-op.",
        },
    };

    void Report(string message)
    {
        Notice = message;
        ModConsole.Error("Co-op: " + message);
    }

    void Warn(string message)
    {
        Notice = message;
        ModConsole.Warning("Co-op: " + message);
    }

    void OnLobbyMemberLeft(ulong user)
    {
        if (session == null || !session.IsHost)
            return;
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == user)
                connections[i].LastReceived = double.NegativeInfinity;
        }
    }

    void ApplySimulation()
    {
        if (sim == null)
            return;
        sim.Latency = simLatency;
        sim.Jitter = simJitter;
        sim.Loss = simLoss;
    }

    bool RequireSteam()
    {
        if (steam != null)
            return true;
        Report("Steam is not available.");
        return false;
    }

    bool RequireIdle()
    {
        if (!Active)
            return true;
        Report("Already in a session, leave it first.");
        return false;
    }

    void Update()
    {
        profile.Start();
        steam?.Update(Now);
        session?.Update(Now);
        flow.Update();
        passenger.Update();
        players.Update(Time.deltaTime);
        objects.Update();
        world.Update();
        fsms.Update();
        money.Update();
        shared.Update();
        spawns.Update();
        hoist.Update();
        profile.Stop();
        profiledFrames++;
        if (overlay && Time.unscaledTime >= nextStatus)
        {
            nextStatus = Time.unscaledTime + StatusInterval;
            profiledMs = profile.Elapsed.TotalMilliseconds / Math.Max(1, profiledFrames);
            profile.Reset();
            profiledFrames = 0;
            statusText = BuildStatus();
        }
    }

    public void OnGameReady()
    {
        objects.OnGameReady();
        fsms.OnGameReady();
        world.OnGameReady();
        money.OnGameReady();
        phone.OnGameReady();
        spawns.OnGameReady();
        hoist.OnGameReady();
        shared.OnGameReady();
        flow.OnGameReady();
        players.OnGameReady();
        passenger.OnGameReady();
        if (session != null)
            session.HoldGameMessages = false;
        // A death in mortal mode deletes the save, and a desync can still kill.
        var permadeath = PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerPermaDeath");
        if (session != null && session.IsHost && permadeath != null && permadeath.Value)
            Warn("Mortal mode: dying deletes this save. It was backed up when the game started and can be restored from the co-op panel in the main menu.");
    }

    void FixedUpdate()
    {
        profile.Start();
        objects.FixedUpdate();
        profile.Stop();
    }

    void LateUpdate()
    {
        profile.Start();
        players.LateUpdate(Time.deltaTime);
        profile.Stop();
    }

    // Sent by mods that add objects to the scene from their own OnLevelWasLoaded, whose order
    // against this one is not defined, so every machine identifies the same objects.
    void OnSceneCloned() => objects.OnSceneCloned();

    void OnLevelWasLoaded(int level)
    {
        if (session != null)
            session.HoldGameMessages = Application.loadedLevelName == "GAME";
        if (session != null && session.IsHost)
            lobby?.SetJoinable(Application.loadedLevelName == "MainMenu");
        flow.OnLevelLoaded();
        players.OnLevelLoaded();
        passenger.OnLevelLoaded();
        objects.OnLevelLoaded();
        world.OnLevelLoaded();
        money.OnLevelLoaded();
        phone.OnLevelLoaded();
        spawns.OnLevelLoaded();
        hoist.OnLevelLoaded();
        shared.OnLevelLoaded();
    }

    void OnGUI()
    {
        if (overlay && Active)
            GUI.Label(new Rect(10, 10, 420, 160), statusText);
        menu.Draw();
    }

    string BuildStatus()
    {
        if (!Active)
            return "";
        status.Length = 0;
        status.Append("MSC Co-op ").Append(session.IsHost ? "host" : "client").Append(" | ").Append(session.State).AppendLine();
        var slots = session.Players;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null)
                continue;
            status.Append(i).Append("  ").Append(slots[i].Name);
            var c = FindConnection(slots[i].Id);
            if (c != null)
            {
                status.Append("  rtt ").Append((int)(c.Clock.Rtt * 1000)).Append(" ms");
                status.Append("  in ").Append(c.Stats.KbpsIn.ToString("0.0")).Append(" out ").Append(c.Stats.KbpsOut.ToString("0.0")).Append(" kbps");
            }
            status.AppendLine();
        }
        if (fsms != null)
            status.Append("fsm sent ").Append(fsms.Sent).Append(" applied ").Append(fsms.Applied).Append(" unknown ").Append(fsms.Unknown).Append(" deferred ").Append(fsms.Deferred).AppendLine();
        // Everything this mod does per frame, physics steps included, so its share of a frame is visible.
        status.Append("mod ").Append(profiledMs.ToString("0.00")).Append(" ms/frame").AppendLine();
        if (sim != null && sim.Enabled)
            status.Append("sim ").Append((int)(simLatency * 1000)).Append(" ms +").Append((int)(simJitter * 1000)).Append(" ms ").Append((int)(simLoss * 100)).Append("% loss");
        return status.ToString();
    }

    public Connection FindConnection(ulong id)
    {
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == id)
                return connections[i];
        }
        return null;
    }

    // Steam's build id is missing when the game runs outside Steam. The game assembly's MVID changes with every game update.
    static uint ComputeGameBuild()
    {
        var mvid = typeof(CarDynamics).Assembly.ManifestModule.ModuleVersionId.ToByteArray();
        return Fnv.Hash32(mvid, 0, mvid.Length);
    }

    void OnApplicationQuit() => Leave();

    // MSCLoader destroys every object, DontDestroyOnLoad ones included, when the game returns to the main menu.
    void OnDestroy()
    {
        Leave();
        lobby?.Dispose();
        steam?.Dispose();
        lan?.Dispose();
    }
}
