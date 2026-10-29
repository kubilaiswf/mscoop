using System;
using System.Globalization;
using System.Net;
using MSCCoop.Net;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace MSCCoop;

public class CoopMod : Mod
{
    public override string ID => "MSCCoop";
    public override string Name => "MSC Co-op";
    public override string Version => "0.1.0";
    public override string Author => "kubilay";
    public override string Description => "Co-op multiplayer over Steam.";

    NetRunner runner;
    bool commandAdded;

    public override void ModSetup()
    {
        SetupFunction(Setup.OnMenuLoad, Init);
        SetupFunction(Setup.PostLoad, () => runner.OnGameReady());
    }

    void Init()
    {
        if (runner != null)
            return;
        var go = new GameObject("MSCCoop");
        UnityEngine.Object.DontDestroyOnLoad(go);
        runner = go.AddComponent<NetRunner>();
        runner.Init(Version, ModLoader.GetModSettingsFolder(this));
        // Console commands outlive the runner, which is recreated on every return to the menu.
        if (commandAdded)
            return;
        commandAdded = true;
        ConsoleCommand.Add(new CoopCommand(() => runner));
    }
}

class CoopCommand(Func<NetRunner> current) : ConsoleCommand
{
    public override string Name => "coop";

    public override string Help =>
        "coop host | coop host lan [port] | coop join lan IP [port] | coop invite | coop leave | " +
        "coop sim LATENCY_MS JITTER_MS LOSS_PERCENT | coop overlay | coop dump | coop log on|off | coop version";

    public override void Run(string[] rawArgs)
    {
        var runner = current();
        var args = Array.FindAll(rawArgs, a => a.Length > 0);
        var verb = args.Length > 0 ? args[0] : "";
        var lan = args.Length > 1 && args[1] == "lan";
        switch (verb)
        {
            case "host" when lan:
                runner.HostLan(Port(args, 2));
                break;
            case "host":
                runner.HostSteam(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.MaxPlayers);
                break;
            case "join" when lan && args.Length > 2 && IPAddress.TryParse(args[2], out var address):
                runner.JoinLan(address, Port(args, 3));
                break;
            case "invite":
                runner.Invite();
                break;
            case "leave":
                runner.Leave();
                break;
            case "sim" when args.Length == 4:
                runner.SetSimulation(Number(args[1]), Number(args[2]), Number(args[3]));
                break;
            case "overlay":
                runner.ToggleOverlay();
                break;
            case "dump":
                runner.Dump();
                break;
            case "version":
                runner.PrintVersion();
                break;
            case "log" when args.Length > 1:
                runner.SetLogging(args[1] == "on");
                break;
            default:
                ModConsole.Print(Help);
                break;
        }
    }

    static int Port(string[] args, int index) =>
        args.Length > index && int.TryParse(args[index], out var port) ? port : LanTransport.DefaultPort;

    static double Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
