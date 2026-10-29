using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// The host owns time of day and weather. Clients are moved onto the host's two-hour light block
// through the same WAKEUP event the game uses after sleeping, and onto the host's weather through
// the state the game uses when it loads weather from a save. Sleeping only moves time on the host:
// when the host wakes, every client's world wakes with it, and a client that slept is put back on
// the host's time.
sealed class WorldSync
{
    const double TimeInterval = 1;
    const double WeatherInterval = 5;
    const float MinuteTolerance = 1f;
    const float CloudTolerance = 50f;
    const string WeatherEvent = "MSCCOOP_WEATHER";
    const string CrashSitePath = "TRAFFIC/CrashSite";
    const string SpawnCandlesEvent = "SPAWN";
    const int CandleSeed = 1;
    const string WakeEvent = "WAKEUP";

    // Beds, passing out, being knocked out and being arrested all skip time and wake the world.
    static readonly string[] WakeFsms = { "Activate", "BackToHome" };
    static readonly string[] WakeStates = { "Wake up", "Wake up 2" };

    Session session;
    bool ready;
    double nextTime;
    double nextWeather;
    bool hostWoke;
    bool wokeHere;

    PlayMakerFSM color;
    PlayMakerFSM clock;
    FsmInt block;
    FsmFloat blockMinutes;
    FsmFloat clockMinutes;
    FsmInt day;

    PlayMakerFSM weather;
    Transform clouds;
    FsmFloat posX;
    FsmFloat posZ;
    FsmFloat rotation;
    FsmInt weatherType;
    FsmInt cloudId;
    FsmFloat thunderForce;
    FsmFloat offset;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        ready = false;
    }

    public void OnLevelLoaded() => ready = false;

    // In mortal mode candles mark where the player last died, each dropped onto whatever lies under a
    // random spot near it. Placing them again from one seed puts them in the same places everywhere.
    static void PlaceCandles()
    {
        var site = GameObject.Find(CrashSitePath);
        var fsm = site == null ? null : site.GetPlayMaker("Candles");
        if (fsm == null || fsm.ActiveStateName != "State 5" && fsm.ActiveStateName != "State 7")
            return;
        var saved = Random.seed;
        Random.seed = CandleSeed;
        fsm.SendEvent(SpawnCandlesEvent);
        Random.seed = saved;
    }

    public void OnGameReady()
    {
        if (session == null)
            return;
        PlaceCandles();
        var sun = GameObject.Find("MAP/SUN/Pivot/SUN");
        var cloudObject = GameObject.Find("MAP/CloudSystem/Clouds");
        if (sun == null || cloudObject == null)
        {
            ModConsole.Error("Co-op: could not find the sun or the clouds, time and weather will not sync.");
            return;
        }

        color = sun.GetPlayMaker("Color");
        clock = sun.GetPlayMaker("Clock");
        block = color.FsmVariables.FindFsmInt("Time");
        blockMinutes = color.FsmVariables.FindFsmFloat("Minutes");
        clockMinutes = clock.FsmVariables.FindFsmFloat("Minutes");
        day = PlayMakerGlobals.Instance.Variables.FindFsmInt("GlobalDay");

        weather = cloudObject.GetPlayMaker("Weather");
        clouds = cloudObject.transform;
        var v = weather.FsmVariables;
        posX = v.FindFsmFloat("PosX");
        posZ = v.FindFsmFloat("PosZ");
        rotation = v.FindFsmFloat("Rotation");
        weatherType = v.FindFsmInt("WeatherType");
        cloudId = v.FindFsmInt("WeatherCloudID");
        thunderForce = v.FindFsmFloat("ThunderForce");
        offset = v.FindFsmFloat("Offset");
        if (block == null || blockMinutes == null || clockMinutes == null || day == null || posX == null || posZ == null
            || rotation == null || weatherType == null || cloudId == null || thunderForce == null || offset == null)
        {
            ModConsole.Error("Co-op: the sun or weather logic has changed, time and weather will not sync.");
            return;
        }
        weather.AddSharedGlobalTransition(WeatherEvent, "Set cloud");
        // The host announces a new weather roll as soon as it happens instead of at the next interval.
        weather.FsmInject("Move clouds", () => nextWeather = 0, index: 0);
        HookWakes();
        ready = true;
    }

    void HookWakes()
    {
        foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
        {
            if (fsm.gameObject.hideFlags != HideFlags.None || System.Array.IndexOf(WakeFsms, fsm.FsmName) < 0)
                continue;
            foreach (var state in WakeStates)
            {
                if (fsm.GetState(state) != null)
                    fsm.FsmInject(state, OnLocalWake, index: 0);
            }
        }
    }

    // Runs just before the wake state moves the clock, so the host sends the new time a frame later.
    void OnLocalWake()
    {
        if (session == null)
            return;
        if (session.IsHost)
            hostWoke = true;
        else
            wokeHere = true;
    }

    // The game broadcasts WAKEUP to every FSM after a sleep. The player's own ones (drunkenness, the
    // bladder, the wanted level) only wake for whoever slept.
    static void WakeWorld()
    {
        foreach (var fsm in new List<PlayMakerFSM>(PlayMakerFSM.FsmList))
        {
            if (fsm != null && fsm.transform.root.name != "PLAYER" && fsm.gameObject.name != "PlayerWanted")
                fsm.SendEvent(WakeEvent);
        }
    }

    public void Update()
    {
        if (!ready || session == null || !session.IsHost || session.State != SessionState.Connected)
            return;
        var now = session.Now;
        if (now >= nextTime || hostWoke)
        {
            nextTime = now + TimeInterval;
            var w = session.BeginMessage(hostWoke ? MessageType.WorldWake : MessageType.WorldTime);
            hostWoke = false;
            w.WriteUInt((uint)day.Value);
            w.WriteByte((byte)block.Value);
            w.WriteFloat(blockMinutes.Value);
            w.WriteFloat(clockMinutes.Value);
            session.Broadcast(Channel.Control);
        }
        if (now >= nextWeather)
        {
            nextWeather = now + WeatherInterval;
            var w = session.BeginMessage(MessageType.WorldWeather);
            w.WriteFloat(posX.Value);
            w.WriteFloat(posZ.Value);
            w.WriteFloat(rotation.Value);
            w.WriteByte((byte)weatherType.Value);
            w.WriteByte((byte)cloudId.Value);
            w.WriteFloat(thunderForce.Value);
            w.WriteFloat(offset.Value);
            session.Broadcast(Channel.Control);
        }
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        if (session.IsHost)
            return;
        switch (type)
        {
            case MessageType.WorldTime:
            case MessageType.WorldWake:
                var hostDay = (int)r.ReadUInt();
                var hostBlock = r.ReadByte();
                var hostBlockMinutes = r.ReadFloat();
                var hostClockMinutes = r.ReadFloat();
                if (!r.Overflow && ready)
                    ApplyTime(hostDay, hostBlock, hostBlockMinutes, hostClockMinutes, type == MessageType.WorldWake);
                break;
            case MessageType.WorldWeather:
                var x = r.ReadFloat();
                var z = r.ReadFloat();
                var rot = r.ReadFloat();
                var kind = r.ReadByte();
                var cloud = r.ReadByte();
                var thunder = r.ReadFloat();
                var off = r.ReadFloat();
                if (!r.Overflow && ready)
                    ApplyWeather(x, z, rot, kind, cloud, thunder, off);
                break;
        }
    }

    void ApplyTime(int hostDay, int hostBlock, float hostBlockMinutes, float hostClockMinutes, bool woke)
    {
        day.Value = hostDay;
        if (block.Value != hostBlock || woke)
        {
            block.Value = hostBlock;
            if (woke || wokeHere)
            {
                WakeWorld();
            }
            else
            {
                color.SendEvent(WakeEvent);
                clock.SendEvent(WakeEvent);
            }
        }
        wokeHere = false;
        if (Mathf.Abs(blockMinutes.Value - hostBlockMinutes) > MinuteTolerance)
        {
            blockMinutes.Value = hostBlockMinutes;
            clockMinutes.Value = hostClockMinutes;
        }
    }

    void ApplyWeather(float x, float z, float rot, int kind, int cloud, float thunder, float off)
    {
        var sameWeather = weatherType.Value == kind && cloudId.Value == cloud;
        if (sameWeather && Mathf.Abs(posX.Value - x) < CloudTolerance)
            return;

        posX.Value = x;
        posZ.Value = z;
        offset.Value = off;
        if (sameWeather)
            return;

        rotation.Value = rot;
        weatherType.Value = kind;
        cloudId.Value = cloud;
        thunderForce.Value = thunder;
        clouds.position = new Vector3(x, clouds.position.y, z);
        clouds.rotation = Quaternion.Euler(0, rot, 0);
        weather.SendEvent(WeatherEvent);
    }
}
