using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using Steamworks;
using UnityEngine;

namespace MSCCoop;

// Hosting, joining and inviting from the main menu, and the running session from the pause menu.
sealed class CoopMenu(NetRunner runner)
{
    const int WindowId = 0x4D534343;
    const float Width = 340;
    const float Margin = 16;
    const float ReferenceHeight = 900;
    const float FriendRefreshSeconds = 2;
    const float PublicRefreshSeconds = 30;
    const int MinPlayers = 2;
    const string MenuScene = "MainMenu";
    const string GameScene = "GAME";

    static readonly string[] Tabs = { "Host", "Join" };
    static readonly string[] LobbyTypes = { "Friends", "Public", "Invite only" };
    static readonly string[] LobbyTypeHelp =
    {
        "Your Steam friends see the lobby and can join it.",
        "Anyone with the same mod version can find it.",
        "Only the friends you invite can join.",
    };
    static readonly ELobbyType[] LobbyTypeValues =
    {
        ELobbyType.k_ELobbyTypeFriendsOnly, ELobbyType.k_ELobbyTypePublic, ELobbyType.k_ELobbyTypePrivate,
    };
    static readonly string[] PlayerCounts = { "2", "3", "4" };

    int tab;
    int lobbyType;
    int playerCount = Protocol.MaxPlayers - MinPlayers;
    string lanAddress = "127.0.0.1";
    Vector2 inviteScroll;
    float nextFriendRefresh;
    float nextPublicRefresh;
    readonly HashSet<ulong> invited = new();
    ulong invitedLobby;
    bool showBackups;
    string[] backups;
    string restoreAsked;
    FsmBool paused;
    GUIStyle header, dim, notice;
    Action pending;

    public void Draw()
    {
        // GUILayout lays a window out once and then draws it for each event, so anything that changes
        // what the window shows waits for the next layout pass.
        if (pending != null && Event.current.type == EventType.Layout)
        {
            var action = pending;
            pending = null;
            action();
        }

        var scene = Application.loadedLevelName;
        if (scene == GameScene ? !runner.Active || !Paused() : scene != MenuScene)
            return;
        CreateStyles();
        var scale = Screen.height / ReferenceHeight;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        var x = Screen.width / scale - Width - Margin;
        GUILayout.Window(WindowId, new Rect(x, Margin, Width, 0), DrawWindow, "MSC Co-op");
        GUI.matrix = Matrix4x4.identity;
    }

    bool Paused()
    {
        paused ??= PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerInMenu");
        return paused.Value;
    }

    void CreateStyles()
    {
        if (header != null)
            return;
        header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        dim = new GUIStyle(GUI.skin.label) { wordWrap = true };
        dim.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
        notice = new GUIStyle(GUI.skin.label) { wordWrap = true };
        notice.normal.textColor = new Color(1f, 0.55f, 0.4f);
    }

    void DrawWindow(int id)
    {
        if (runner.Active)
            DrawSession(runner.Session);
        else
            DrawStart();
        if (runner.Notice.Length > 0)
            GUILayout.Label(runner.Notice, notice);
    }

    void DrawStart()
    {
        var selected = GUILayout.Toolbar(tab, Tabs);
        if (selected != tab)
            pending = () => tab = selected;
        GUILayout.Space(6);
        if (tab == 0)
            DrawHost();
        else
            DrawJoin();
        GUILayout.Space(10);
        DrawBackups();
    }

    void DrawHost()
    {
        if (runner.Lobby == null)
        {
            GUILayout.Label("Steam is not running, only local network play works.", dim);
        }
        else
        {
            GUILayout.Label("Who can join", header);
            lobbyType = GUILayout.Toolbar(lobbyType, LobbyTypes);
            GUILayout.Label(LobbyTypeHelp[lobbyType], dim);
            GUILayout.Label("Players", header);
            playerCount = GUILayout.Toolbar(playerCount, PlayerCounts);
            GUILayout.Space(6);
            if (GUILayout.Button("Create lobby", GUILayout.Height(30)))
                pending = () => runner.HostSteam(LobbyTypeValues[lobbyType], MinPlayers + playerCount);
        }
        GUILayout.Space(6);
        if (GUILayout.Button("Host on local network"))
            pending = () => runner.HostLan(LanTransport.DefaultPort);
    }

    void DrawJoin()
    {
        var lobby = runner.Lobby;
        if (lobby != null)
        {
            if (Event.current.type == EventType.Layout && Time.unscaledTime >= nextFriendRefresh)
            {
                nextFriendRefresh = Time.unscaledTime + FriendRefreshSeconds;
                lobby.RefreshFriends();
            }
            if (Event.current.type == EventType.Layout && Time.unscaledTime >= nextPublicRefresh)
            {
                nextPublicRefresh = Time.unscaledTime + PublicRefreshSeconds;
                lobby.SearchPublic();
            }
            GUILayout.Label("Friends", header);
            DrawListings(lobby.FriendLobbies, "None of your friends is hosting right now.");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Public lobbies", header);
            GUI.enabled = !lobby.Searching;
            if (GUILayout.Button(lobby.Searching ? "Searching" : "Refresh", GUILayout.Width(90)))
                nextPublicRefresh = 0;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            DrawListings(lobby.PublicLobbies, "No public lobbies right now.");
        }
        GUILayout.Label("Local network", header);
        GUILayout.BeginHorizontal();
        lanAddress = GUILayout.TextField(lanAddress);
        if (GUILayout.Button("Join", GUILayout.Width(70)))
            pending = JoinLan;
        GUILayout.EndHorizontal();
    }

    void JoinLan()
    {
        if (IPAddress.TryParse(lanAddress.Trim(), out var address))
            runner.JoinLan(address, LanTransport.DefaultPort);
    }

    void DrawListings(List<SteamLobby.Listing> listings, string empty)
    {
        if (listings.Count == 0)
        {
            GUILayout.Label(empty, dim);
            return;
        }
        foreach (var listing in listings)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(listing.Members > 0 ? listing.Host + "   " + listing.Members + "/" + listing.Limit : listing.Host);
            GUI.enabled = listing.Compatible;
            if (GUILayout.Button(listing.Compatible ? "Join" : "Other version", GUILayout.Width(100)))
            {
                var id = listing.Id;
                pending = () => runner.JoinSteam(id);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
    }

    void DrawSession(Session session)
    {
        var connected = session.State == SessionState.Connected;
        GUILayout.Label(session.IsHost ? "Hosting" : connected ? "Connected" : "Connecting...", header);
        var players = session.Players;
        for (var i = 0; i < players.Length; i++)
        {
            if (players[i] == null)
                continue;
            GUILayout.BeginHorizontal();
            GUILayout.Label(players[i].Name + (i == 0 ? "  (host)" : "") + (i == session.LocalSlot ? "  (you)" : ""));
            var connection = runner.FindConnection(players[i].Id);
            if (connection != null)
                GUILayout.Label((int)(connection.Clock.Rtt * 1000) + " ms", dim, GUILayout.Width(60));
            GUILayout.EndHorizontal();
        }

        var inMenu = Application.loadedLevelName == MenuScene;
        if (inMenu && connected)
            GUILayout.Label(session.IsHost ? "Click Continue or New Game when everyone is here." : "Waiting for the host to start the game.", dim);
        var lobby = runner.Lobby;
        if (inMenu && session.IsHost && lobby != null && lobby.Current != 0)
            DrawInvites(lobby);
        GUILayout.Space(6);
        if (GUILayout.Button("Leave", GUILayout.Height(28)))
            pending = runner.Leave;
    }

    void DrawInvites(SteamLobby lobby)
    {
        if (Event.current.type == EventType.Layout && Time.unscaledTime >= nextFriendRefresh)
        {
            nextFriendRefresh = Time.unscaledTime + FriendRefreshSeconds;
            lobby.RefreshFriends();
        }
        if (invitedLobby != lobby.Current)
        {
            invitedLobby = lobby.Current;
            invited.Clear();
        }
        GUILayout.Space(6);
        GUILayout.Label("Invite friends", header);
        if (GUILayout.Button("Open Steam invite list"))
            runner.Invite();
        if (lobby.OnlineFriends.Count == 0)
        {
            GUILayout.Label("No friends online.", dim);
            return;
        }
        inviteScroll = GUILayout.BeginScrollView(inviteScroll, GUILayout.Height(150));
        foreach (var friend in lobby.OnlineFriends)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(friend.Value);
            var sent = invited.Contains(friend.Key);
            GUI.enabled = !sent;
            if (GUILayout.Button(sent ? "Invited" : "Invite", GUILayout.Width(80)))
            {
                lobby.InviteFriend(friend.Key);
                invited.Add(friend.Key);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    void DrawBackups()
    {
        var show = GUILayout.Toggle(showBackups, " Save backups");
        if (show != showBackups)
            pending = () => SetShowBackups(show);
        if (!showBackups)
            return;
        backups ??= runner.Flow.Backups();
        if (backups.Length == 0)
        {
            GUILayout.Label("None yet. The save is backed up every time a hosted game starts.", dim);
            return;
        }
        foreach (var backup in backups)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(BackupName(backup));
            var asked = restoreAsked == backup;
            if (GUILayout.Button(asked ? "Sure?" : "Restore", GUILayout.Width(80)))
            {
                if (asked)
                    pending = () => Restore(backup);
                else
                    restoreAsked = backup;
            }
            GUILayout.EndHorizontal();
        }
    }

    void SetShowBackups(bool show)
    {
        showBackups = show;
        backups = null;
        restoreAsked = null;
    }

    void Restore(string backup)
    {
        SetShowBackups(false);
        runner.Flow.Restore(backup);
    }

    static string BackupName(string dir)
    {
        var name = Path.GetFileName(dir);
        return DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("d MMM yyyy  HH:mm", CultureInfo.InvariantCulture)
            : name;
    }
}
