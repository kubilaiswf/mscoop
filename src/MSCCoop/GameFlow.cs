using System;
using System.Collections.Generic;
using System.IO;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Starts every player into the host's save. The host's Continue click is mirrored to clients,
// and clients read and write saves in a scratch folder so their own save is never touched. A new
// game on the host is mirrored the same way once the game has written its fresh save: clients skip
// the intro and load that save. The host's save is backed up first.
sealed class GameFlow(string clientSaveDir, string backupDir)
{
    const string MenuScene = "MainMenu";
    const string GameScene = "GAME";
    const string ContinueState = "Reset globals 2";
    const string ContinueEvent = "MSCCOOP_CONTINUE";
    const string OwnOptionsFile = "options.txt";
    const string NewGameState = "State 1";
    const string BeginButton = "Licence/Buttons/ButtonBegin";
    const int BackupsKept = 10;

    readonly SaveReceiver receiver = new();
    Session session;
    SessionConfig config;
    PlayMakerFSM continueFsm;
    PlayMakerFSM beginFsm;
    Collider continueButton;
    Collider newGameButton;
    bool saveReady;
    bool startingFromHost;
    bool saveSent;
    bool returnToMenu;

    static bool InMenu => Application.loadedLevelName == MenuScene;
    static bool InGame => Application.loadedLevelName == GameScene;

    public bool CanJoin()
    {
        if (InMenu)
            return true;
        ModConsole.Error("Co-op: join from the main menu.");
        return false;
    }

    public void Attach(Session newSession, SessionConfig newConfig)
    {
        session = newSession;
        config = newConfig;
        config.AcceptingJoins = !InGame;
        receiver.Reset();
        saveReady = false;
        startingFromHost = false;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        var wasClient = session != null && !session.IsHost;
        session = null;
        SetMenuButtons(true);
        if (!wasClient)
            return;

        // A session can drop while the host's save is loading; clearing the redirect then would let the
        // client continue into its own save, so it is only cleared once the client is back in the menu.
        if (InMenu && !startingFromHost)
            ES2GlobalSettings.defaultPCDataPath = "";
        else
            returnToMenu = true;
    }

    public void OnLevelLoaded()
    {
        if (InMenu)
        {
            if (session == null || session.IsHost)
                ES2GlobalSettings.defaultPCDataPath = "";
            returnToMenu = false;
            HookMenu();
        }
        if (config != null)
            config.AcceptingJoins = InMenu && session != null && session.IsHost;

        if (InGame && session != null && !session.IsHost && !startingFromHost)
        {
            ModConsole.Error("Co-op: loaded a game without the host, leaving the session.");
            session.Leave();
        }
        startingFromHost = false;
    }

    public void OnGameReady()
    {
        if (session == null)
            return;
        if (!session.IsHost)
            DisableSaving();
        session.BeginMessage(MessageType.Ready);
        session.Broadcast(Channel.Control);
    }

    // Saving quits to the menu. On a client it would write the scratch copy of the host's save and
    // drop out of the session, so only the host can save.
    static void DisableSaving()
    {
        foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
        {
            if (fsm.gameObject.name == "SAVEGAME" && fsm.FsmName == "Button" && fsm.gameObject.hideFlags == HideFlags.None)
                fsm.enabled = false;
        }
    }

    public void Update()
    {
        if (InMenu)
            SetMenuButtons(session == null || session.IsHost);

        if (returnToMenu && InGame && !Application.isLoadingLevel)
        {
            returnToMenu = false;
            ModConsole.Print("Co-op: returning to the main menu.");
            Application.LoadLevel(MenuScene);
        }
    }

    void HookMenu()
    {
        var button = GameObject.Find("Interface/Buttons/ButtonContinue");
        var fsm = button.GetPlayMaker("SetSize");
        if (fsm == continueFsm)
            return;
        continueFsm = fsm;
        saveSent = false;
        continueButton = button.GetComponent<Collider>();
        newGameButton = GameObject.Find("Interface/Buttons/ButtonNewgame").GetComponent<Collider>();
        continueFsm.FsmInject(ContinueState, OnContinue, index: 0);
        continueFsm.FsmInject(ContinueState, BackUpSave, index: 0);
        continueFsm.AddSharedGlobalTransition(ContinueEvent, ContinueState);
        // The licence card that starts a new game is switched off until New Game is clicked, and its
        // FSMs only wake then.
        var newGame = GameObject.Find("Interface/Buttons/ButtonNewgame").GetPlayMaker("SetSize");
        newGame.FsmInject("State 1", HookNewGame, index: newGame.GetState("State 1").Actions.Length);
    }

    void HookNewGame()
    {
        BackUpSave();
        var licence = GameObject.Find(BeginButton);
        var fsm = licence == null ? null : licence.GetPlayMaker("SetSize");
        if (fsm == null || fsm == beginFsm)
            return;
        beginFsm = fsm;
        // Entered after the old save is deleted and the new player's details are written, just
        // before the intro loads.
        beginFsm.FsmInject(NewGameState, OnContinue, index: 0);
    }

    // A session can still break things that end up in the save, and a new game or a death in mortal
    // mode deletes it, so the host's save is copied aside before every game a session starts.
    void BackUpSave()
    {
        if (session == null || !session.IsHost)
            return;
        var dir = Path.Combine(backupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var path in Directory.GetFiles(Application.persistentDataPath, "*.txt"))
                File.Copy(path, Path.Combine(dir, Path.GetFileName(path)), true);
            var backups = Directory.GetDirectories(backupDir);
            Array.Sort(backups, StringComparer.Ordinal);
            for (var i = 0; i < backups.Length - BackupsKept; i++)
                Directory.Delete(backups[i], true);
            ModConsole.Print("Co-op: save backed up to " + dir);
        }
        catch (Exception e)
        {
            ModConsole.Warning("Co-op: could not back up the save. " + e.Message);
        }
    }

    void SetMenuButtons(bool enabled)
    {
        if (continueButton == null)
            return;
        continueButton.enabled = enabled;
        newGameButton.enabled = enabled;
    }

    void OnContinue()
    {
        if (session == null || !session.IsHost || saveSent)
            return;
        saveSent = true;

        var files = ReadSave(Application.persistentDataPath);
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (!connections[i].Established)
                continue;
            SaveTransfer.Send(session, connections[i], files);
            session.BeginMessage(MessageType.StartGame);
            session.Send(connections[i], Channel.Bulk);
            connections[i].Loading = true;
        }
        config.AcceptingJoins = false;
        ModConsole.Print("Co-op: sent save to " + connections.Count + " player(s).");
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        switch (type)
        {
            case MessageType.SaveBegin when !session.IsHost:
                receiver.Reset();
                saveReady = false;
                break;
            case MessageType.SaveChunk when !session.IsHost:
                if (!receiver.HandleChunk(reader))
                    Fail("received a malformed save chunk.");
                break;
            case MessageType.SaveComplete when !session.IsHost:
                if (receiver.Complete(reader, out var files))
                {
                    WriteClientSave(files);
                    saveReady = true;
                }
                else
                {
                    Fail("the host's save did not arrive intact.");
                }
                break;
            case MessageType.StartGame when !session.IsHost:
                if (!saveReady || !InMenu)
                {
                    Fail("the host started the game before the save was ready.");
                    break;
                }
                ES2GlobalSettings.defaultPCDataPath = clientSaveDir;
                startingFromHost = true;
                from.Loading = true;
                continueFsm.SendEvent(ContinueEvent);
                break;
            case MessageType.Ready:
                from.Loading = false;
                if (session.IsHost)
                    ModConsole.Print("Co-op: player in slot " + from.Slot + " finished loading.");
                break;
        }
    }

    void Fail(string reason)
    {
        ModConsole.Error("Co-op: " + reason);
        session.Leave();
    }

    static List<SaveFile> ReadSave(string dir)
    {
        var files = new List<SaveFile>();
        foreach (var path in Directory.GetFiles(dir, "*.txt"))
        {
            var name = Path.GetFileName(path);
            if (name != OwnOptionsFile && SaveTransfer.IsSafeName(name))
                files.Add(new SaveFile(name, File.ReadAllBytes(path)));
        }
        return files;
    }

    void WriteClientSave(List<SaveFile> files)
    {
        Directory.CreateDirectory(clientSaveDir);
        foreach (var old in Directory.GetFiles(clientSaveDir, "*.txt"))
            File.Delete(old);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(clientSaveDir, file.Name), file.Data);

        var ownOptions = Path.Combine(Application.persistentDataPath, OwnOptionsFile);
        if (File.Exists(ownOptions))
            File.Copy(ownOptions, Path.Combine(clientSaveDir, OwnOptionsFile), true);
    }
}
