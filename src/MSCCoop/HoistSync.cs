using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// The engine hoist's arm rises a little every frame the pump handle is held, so it cannot be
// replayed as steps. Its angle is sent while it changes, and set on the arm the way the pump does.
sealed class HoistSync
{
    const string HoistPath = "ITEMS/motor hoist(itemx)";
    const double SendInterval = 0.1;
    const float Tolerance = 0.01f;

    Session session;
    FsmFloat angle;
    Transform arm;
    float sent;
    double nextSend;

    bool Active => angle != null && session != null && session.State == SessionState.Connected;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        angle = null;
    }

    public void OnLevelLoaded() => angle = null;

    public void OnGameReady()
    {
        if (session == null)
            return;
        var hoist = GameObject.Find(HoistPath);
        var pump = hoist == null ? null : hoist.transform.Find("Pump/Trigger");
        arm = hoist == null ? null : hoist.transform.Find("motorhoist_arm");
        var fsm = pump == null ? null : pump.gameObject.GetPlayMaker("Usage");
        angle = fsm == null || arm == null ? null : fsm.FsmVariables.FindFsmFloat("Angle");
        if (angle != null)
            sent = angle.Value;
    }

    public void Update()
    {
        if (!Active || session.Now < nextSend || Mathf.Abs(angle.Value - sent) < Tolerance)
            return;
        nextSend = session.Now + SendInterval;
        sent = angle.Value;
        session.BeginMessage(MessageType.HoistAngle).WriteFloat(sent);
        session.Broadcast(Channel.Control);
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        if (type != MessageType.HoistAngle)
            return;
        var value = r.ReadFloat();
        if (r.Overflow || float.IsNaN(value) || angle == null)
            return;
        // Clients only talk to the host, so the host forwards the angle to the others.
        if (session.IsHost && session.Connections.Count > 1)
        {
            session.BeginMessage(MessageType.HoistAngle).WriteFloat(value);
            session.Broadcast(Channel.Control, from);
        }
        sent = value;
        angle.Value = value;
        var euler = arm.localEulerAngles;
        arm.localEulerAngles = new Vector3(value, euler.y, euler.z);
    }
}
