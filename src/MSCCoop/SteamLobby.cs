using System;
using System.Collections.Generic;
using MSCCoop.Net;
using Steamworks;

namespace MSCCoop;

sealed class SteamLobby
{
    const string VersionKey = "mscc_version";
    const string BuildKey = "mscc_build";
    const string ProtocolKey = "mscc_protocol";
    const string HostKey = "mscc_host";

    public sealed class Listing
    {
        public ulong Id;
        public string Host;
        public int Members;
        public int Limit;
        public bool Compatible;
    }

    public readonly List<Listing> FriendLobbies = new();
    public readonly List<Listing> PublicLobbies = new();
    public readonly List<KeyValuePair<ulong, string>> OnlineFriends = new();

    readonly string modVersion;
    readonly uint gameBuild;
    readonly string hostName;
    readonly CallResult<LobbyCreated_t> created;
    readonly CallResult<LobbyMatchList_t> listed;
    readonly Callback<LobbyEnter_t> entered;
    readonly Callback<GameLobbyJoinRequested_t> joinRequested;
    readonly Callback<LobbyChatUpdate_t> memberChanged;

    public SteamLobby(string modVersion, uint gameBuild, string hostName)
    {
        this.modVersion = modVersion;
        this.gameBuild = gameBuild;
        this.hostName = hostName;
        created = CallResult<LobbyCreated_t>.Create(OnCreated);
        listed = CallResult<LobbyMatchList_t>.Create(OnListed);
        entered = Callback<LobbyEnter_t>.Create(OnEntered);
        joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => Join(r.m_steamIDLobby.m_SteamID));
        memberChanged = Callback<LobbyChatUpdate_t>.Create(OnMemberChanged);
    }

    public event Action Hosted;
    public event Action<ulong> JoinedAsClient;
    public event Action<string> Failed;
    public event Action<ulong> MemberLeft;

    public ulong Current { get; private set; }

    public bool Searching { get; private set; }

    public bool IsMember(ulong user)
    {
        if (Current == 0)
            return false;
        var lobby = new CSteamID(Current);
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (var i = 0; i < count; i++)
        {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == user)
                return true;
        }
        return false;
    }

    public void Create(ELobbyType type, int maxPlayers) => created.Set(SteamMatchmaking.CreateLobby(type, maxPlayers));

    public void Join(ulong lobby)
    {
        Leave();
        SteamMatchmaking.JoinLobby(new CSteamID(lobby));
    }

    public void Invite()
    {
        if (Current != 0)
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Current));
    }

    public void InviteFriend(ulong friend)
    {
        if (Current != 0)
            SteamMatchmaking.InviteUserToLobby(new CSteamID(Current), new CSteamID(friend));
    }

    // Joins are only accepted in the main menu, so the lobby is closed while the game runs.
    public void SetJoinable(bool joinable)
    {
        if (Current != 0)
            SteamMatchmaking.SetLobbyJoinable(new CSteamID(Current), joinable);
    }

    // Friends in this game who sit in a lobby show up here once Steam has sent that lobby's data; a
    // lobby without it belongs to something else than this mod.
    public void RefreshFriends()
    {
        FriendLobbies.Clear();
        OnlineFriends.Clear();
        var app = SteamUtils.GetAppID().m_AppId;
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (var i = 0; i < count; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (SteamFriends.GetFriendPersonaState(friend) == EPersonaState.k_EPersonaStateOffline)
                continue;
            var name = SteamFriends.GetFriendPersonaName(friend);
            OnlineFriends.Add(new KeyValuePair<ulong, string>(friend.m_SteamID, name));
            if (!SteamFriends.GetFriendGamePlayed(friend, out var game) || game.m_gameID.AppID().m_AppId != app)
                continue;
            var lobby = game.m_steamIDLobby;
            if (!lobby.IsValid() || lobby.m_SteamID == Current)
                continue;
            if (SteamMatchmaking.GetLobbyData(lobby, ProtocolKey).Length == 0)
                SteamMatchmaking.RequestLobbyData(lobby);
            else
                FriendLobbies.Add(Describe(lobby, name));
        }
        OnlineFriends.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));
    }

    public void SearchPublic()
    {
        if (Searching)
            return;
        Searching = true;
        SteamMatchmaking.AddRequestLobbyListStringFilter(ProtocolKey, Protocol.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        listed.Set(SteamMatchmaking.RequestLobbyList());
    }

    void OnListed(LobbyMatchList_t result, bool ioFailure)
    {
        Searching = false;
        PublicLobbies.Clear();
        if (ioFailure)
            return;
        for (var i = 0; i < (int)result.m_nLobbiesMatching; i++)
        {
            var lobby = SteamMatchmaking.GetLobbyByIndex(i);
            if (lobby.m_SteamID != Current)
                PublicLobbies.Add(Describe(lobby, SteamMatchmaking.GetLobbyData(lobby, HostKey)));
        }
    }

    Listing Describe(CSteamID lobby, string host) => new()
    {
        Id = lobby.m_SteamID,
        Host = host,
        Members = SteamMatchmaking.GetNumLobbyMembers(lobby),
        Limit = SteamMatchmaking.GetLobbyMemberLimit(lobby),
        Compatible = SteamMatchmaking.GetLobbyData(lobby, ProtocolKey) == Protocol.Version.ToString()
            && SteamMatchmaking.GetLobbyData(lobby, VersionKey) == modVersion
            && SteamMatchmaking.GetLobbyData(lobby, BuildKey) == gameBuild.ToString(),
    };

    public void Leave()
    {
        if (Current == 0)
            return;
        SteamMatchmaking.LeaveLobby(new CSteamID(Current));
        Current = 0;
    }

    void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            Failed?.Invoke("Could not create lobby: " + result.m_eResult);
            return;
        }
        Current = result.m_ulSteamIDLobby;
        var lobby = new CSteamID(Current);
        SteamMatchmaking.SetLobbyData(lobby, VersionKey, modVersion);
        SteamMatchmaking.SetLobbyData(lobby, BuildKey, gameBuild.ToString());
        SteamMatchmaking.SetLobbyData(lobby, ProtocolKey, Protocol.Version.ToString());
        SteamMatchmaking.SetLobbyData(lobby, HostKey, hostName);
        Hosted?.Invoke();
    }

    void OnEntered(LobbyEnter_t result)
    {
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        var owner = SteamMatchmaking.GetLobbyOwner(lobby);
        if (owner == SteamUser.GetSteamID())
            return;

        Current = result.m_ulSteamIDLobby;
        var hostVersion = SteamMatchmaking.GetLobbyData(lobby, VersionKey);
        var hostBuild = SteamMatchmaking.GetLobbyData(lobby, BuildKey);
        var hostProtocol = SteamMatchmaking.GetLobbyData(lobby, ProtocolKey);
        string problem = null;
        if (hostProtocol != Protocol.Version.ToString() || hostVersion != modVersion)
            problem = "Host runs mod " + hostVersion + " protocol " + hostProtocol + ", you run " + modVersion + " protocol " + Protocol.Version + ". Use the same MSCCoop.dll.";
        else if (hostBuild != gameBuild.ToString())
            problem = "Host runs game build " + hostBuild + ", you run " + gameBuild + ". Both need the same game update.";

        if (problem != null)
        {
            Leave();
            Failed?.Invoke(problem);
            return;
        }
        JoinedAsClient?.Invoke(owner.m_SteamID);
    }

    void OnMemberChanged(LobbyChatUpdate_t update)
    {
        const EChatMemberStateChange gone = EChatMemberStateChange.k_EChatMemberStateChangeLeft
            | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
            | EChatMemberStateChange.k_EChatMemberStateChangeKicked
            | EChatMemberStateChange.k_EChatMemberStateChangeBanned;
        if (update.m_ulSteamIDLobby == Current && ((EChatMemberStateChange)update.m_rgfChatMemberStateChange & gone) != 0)
            MemberLeft?.Invoke(update.m_ulSteamIDUserChanged);
    }

    public void Dispose()
    {
        created.Dispose();
        listed.Dispose();
        entered.Dispose();
        joinRequested.Dispose();
        memberChanged.Dispose();
    }
}
