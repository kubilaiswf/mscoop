using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Mirrors part assembly, removal and bolting by driving the same PlayMaker states on every machine.
// Bolts are sent as absolute stage and part tightness rather than as turns, so a repeated or late
// message cannot leave a bolt one step off.
sealed class FsmSync(ObjectRegistry registry, ObjectSync objects)
{
    const string AssembleEvent = "MSCCOOP_ASSEMBLE";
    const string AssembleEvent2 = "MSCCOOP_ASSEMBLE2";
    const string RemoveEvent = "MSCCOOP_REMOVE";
    const string StageEvent = "MSCCOOP_STAGE";
    const string ToggleEvent = "MSCCOOP_TOGGLE_";
    const string StepEvent = "MSCCOOP_STEP_";

    // Doors, hoods, lids and dashboard switches: each state is one position, and the game moves the
    // part and switches what it controls on entering it, so entering the same state on every
    // machine reproduces it. The first set whose states all exist wins, so longer sets come first.
    static readonly string[][] TogglePairs =
    {
        new[] { "Open door", "Close door" },
        new[] { "Open hood", "Close hood" },
        new[] { "Open hood 2", "Close hood 2" },
        new[] { "Open", "Close" },
        new[] { "Off", "Shorts", "Longs" },
        new[] { "Longs", "Shorts" },
        new[] { "Off", "Slow", "Fast" },
        new[] { "On", "Off" },
    };

    // The horn sounds for as long as its state is active, which ends on the local mouse release.
    static readonly string[] HornStates = { "State 1", "Mouse over 2" };

    sealed class Bolt
    {
        public FsmInt Stage;
        public FsmFloat Tightness;
    }

    readonly Dictionary<PlayMakerFSM, Bolt> bolts = new();
    readonly HashSet<PlayMakerFSM> turning = new();
    readonly Dictionary<PlayMakerFSM, string[]> toggles = new();
    readonly Dictionary<PlayMakerFSM, int> toggleState = new();
    readonly Dictionary<PlayMakerFSM, Queue<string>> deferred = new();
    readonly List<PlayMakerFSM> deferredFsms = new();
    int deferredCount;
    int frame;
    const int MaxDeferred = 2000;
    const int DeferredCheckInterval = 10;
    Session session;
    bool applying;
    int unsupported;
    FsmFloat money;
    readonly HashSet<PlayMakerFSM> walletSteps = new();
    readonly Dictionary<PlayMakerFSM, List<KeyValuePair<PlayMakerFSM, string>>> payments = new();

    public int Sent { get; private set; }
    public int Applied { get; private set; }
    public int Unknown { get; private set; }

    bool Active => session != null && session.State == SessionState.Connected;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        bolts.Clear();
        turning.Clear();
        toggles.Clear();
        toggleState.Clear();
        deferred.Clear();
        deferredFsms.Clear();
        deferredCount = 0;
    }

    public int Deferred => deferredCount;

    // Events for FSMs that were inactive when they arrived, e.g. doors of a building switched off by
    // distance, are entered once the FSM is running again, in the order they arrived.
    public void Update()
    {
        if (deferredFsms.Count == 0 || ++frame % DeferredCheckInterval != 0)
            return;
        for (var i = deferredFsms.Count - 1; i >= 0; i--)
        {
            var fsm = deferredFsms[i];
            if (fsm != null && !IsRunning(fsm))
                continue;
            var queue = deferred[fsm];
            deferredCount -= queue.Count;
            while (fsm != null && queue.Count > 0)
                Enter(fsm, queue.Dequeue());
            deferred.Remove(fsm);
            deferredFsms.RemoveAt(i);
        }
    }

    static bool IsRunning(PlayMakerFSM fsm) => fsm.enabled && fsm.gameObject.activeInHierarchy && fsm.Fsm.Started;

    public void OnGameReady()
    {
        money = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
        walletSteps.Clear();
        payments.Clear();
        bolts.Clear();
        toggles.Clear();
        toggleState.Clear();
        unsupported = 0;
        foreach (var fsm in registry.Fsms)
            Hook(fsm);
        if (unsupported > 0)
            ModConsole.Warning("Co-op: " + unsupported + " assembly FSMs have an unknown layout and will not sync.");
    }

    public void Hook(PlayMakerFSM fsm)
    {
        switch (fsm.FsmName)
        {
            case "Assembly":
                HookAssembly(fsm);
                break;
            case "Removal":
                HookRemoval(fsm);
                break;
            case "Screw":
                HookScrew(fsm);
                break;
            case "Use" when fsm.gameObject.name == "ButtonHorn":
            case "Horn":
                HookToggle(fsm, HornStates);
                break;
            case "Use" when IsJack(fsm) && fsm.GetState("On terrain") != null:
                HookJack(fsm, IsCarJackStep, "Lift/lift");
                break;
            case "Use" when IsJack(fsm):
                HookJack(fsm, IsFloorJackStep, "Lifter/lift");
                break;
            case "Use" when PayoutState(fsm) is string take:
                HookSteps(fsm, state => state == take);
                walletSteps.Add(fsm);
                break;
            case "Button" when PaymentState(fsm) is string pay:
                HookPayment(fsm, pay);
                break;
            case "Use" when IsShopBox(fsm):
                HookSteps(fsm, IsBoxOpenStep);
                break;
            case "Use" when fsm.FsmVariables.FindFsmString("ID") != null && (fsm.GetState("Destroy") != null || fsm.GetState("Is garbage") != null):
                HookSteps(fsm, IsConsumedStep);
                break;
            case "Use":
                HookToggle(fsm);
                break;
            case "Knob" when fsm.FsmVariables.FindFsmGameObject("Windows") != null:
                HookSteps(fsm, IsWindowStep);
                break;
            case "Usage" when fsm.GetState("Activate dash") != null:
                HookSteps(fsm, IsTurnSignalStep);
                break;
            case "Assemble":
                HookSteps(fsm, IsWireStep);
                break;
            case "BoltCheck" when fsm.GetState("Break off") != null:
                HookSteps(fsm, IsBreakOff);
                break;
        }
    }

    void HookAssembly(PlayMakerFSM fsm)
    {
        var part = fsm.FsmVariables.FindFsmGameObject("Part");
        var hooked = false;
        if (fsm.GetState("Assemble") != null)
        {
            fsm.FsmInject("Assemble", () => OnLocalAssemble(fsm, part, 0), index: 0);
            fsm.AddSharedGlobalTransition(AssembleEvent, "Assemble");
            hooked = true;
        }
        if (fsm.GetState("Assemble 2") != null)
        {
            fsm.FsmInject("Assemble 2", () => OnLocalAssemble(fsm, part, 1), index: 0);
            fsm.AddSharedGlobalTransition(AssembleEvent2, "Assemble 2");
            hooked = true;
        }
        if (!hooked || part == null)
            unsupported++;
    }

    void HookRemoval(PlayMakerFSM fsm)
    {
        var detach = fsm.FsmVariables.FindFsmBool("Detach");
        if (fsm.GetState("Remove part") == null || detach == null)
        {
            unsupported++;
            return;
        }
        fsm.FsmInject("Remove part", () => OnLocalRemove(fsm, detach), index: 0);
        fsm.AddSharedGlobalTransition(RemoveEvent, "Remove part");
    }

    void HookScrew(PlayMakerFSM fsm)
    {
        var stage = fsm.FsmVariables.FindFsmInt("Stage");
        var partVar = fsm.FsmVariables.FindFsmGameObject("PartAssembled");
        if (stage == null || partVar == null || fsm.GetState("Get scroll 2") == null || fsm.GetState("Stages 2") == null)
        {
            HookTurns(fsm);
            return;
        }
        var part = partVar.Value;
        var check = part == null ? null : part.GetPlayMaker("BoltCheck");
        bolts[fsm] = new Bolt { Stage = stage, Tightness = check == null ? null : check.FsmVariables.FindFsmFloat("Tightness") };

        fsm.FsmInject("Screw 2", () => OnLocalTurn(fsm), index: 0);
        fsm.FsmInject("Unscrew 2", () => OnLocalTurn(fsm), index: 0);
        fsm.FsmInject("Get scroll 2", () => OnStageSettled(fsm), index: 0);
        fsm.AddSharedGlobalTransition(StageEvent, "Stages 2");
    }

    void HookToggle(PlayMakerFSM fsm)
    {
        foreach (var states in TogglePairs)
        {
            if (System.Array.TrueForAll(states, s => fsm.GetState(s) != null))
            {
                HookToggle(fsm, states);
                return;
            }
        }
    }

    void HookToggle(PlayMakerFSM fsm, string[] states)
    {
        toggles[fsm] = states;
        for (var i = 0; i < states.Length; i++)
        {
            var index = i;
            fsm.FsmInject(states[i], () => OnLocalToggle(fsm, index), index: 0);
            fsm.AddSharedGlobalTransition(ToggleEvent + i, states[i]);
        }
    }

    void OnLocalToggle(PlayMakerFSM fsm, int index)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        if (toggleState.TryGetValue(fsm, out var current) && current == index)
            return;
        toggleState[fsm] = index;
        var w = session.BeginMessage(MessageType.ToggleState);
        w.WriteUInt(id);
        w.WriteByte((byte)index);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
    }

    void ApplyToggle(uint id, int index)
    {
        var fsm = registry.FindFsm(id);
        if (fsm == null || !toggles.TryGetValue(fsm, out var pair) || index >= pair.Length)
        {
            MarkUnknown("ApplyToggle", id);
            return;
        }
        // Replaying the state the door is already in would snap its animation back and play it again.
        if (toggleState.TryGetValue(fsm, out var current) && current == index)
            return;
        toggleState[fsm] = index;
        Apply(fsm, ToggleEvent + index);
    }

    // Adjusting screws (carburettor, valves, steering wheel bolts) and caps step a value in a Screw or
    // Unscrew state. Each step is deterministic, so replaying the same states in order on every
    // machine over the reliable channel keeps the values equal.
    void HookTurns(PlayMakerFSM fsm) => HookSteps(fsm, IsTurnStep);

    static bool IsTurnStep(string state) => state.StartsWith("Screw") || state.StartsWith("Unscrew");

    // A wire end connects in "Sound", which also tells the other end of the wire to close the loop.
    static bool IsWireStep(string state) => state == "Sound";

    // Each pump of the floor jack or the car jack raises it one fixed step, and lowering is one step
    // too. The car jack checks for terrain before it raises, so its raise is replayed from that check
    // and its own "Up" follows from there.
    static bool IsJack(PlayMakerFSM fsm) => fsm.transform.parent != null && fsm.transform.parent.name.EndsWith("jack(itemx)");

    static bool IsFloorJackStep(string state) => state == "Up" || state == "Down";

    static bool IsCarJackStep(string state) => state == "On terrain" || state == "Up 2";

    // A pump raises the car a few centimetres, too slowly for the car's movement to claim it, so every
    // machine would lift its own copy and the copies drift apart. Whoever pumps takes over the jack
    // and what rests on it, and the others follow.
    void HookJack(PlayMakerFSM fsm, System.Func<string, bool> isStep, string liftPath)
    {
        HookSteps(fsm, isStep);
        var jack = fsm.transform.parent;
        var lift = jack.Find(liftPath);
        if (lift == null)
            return;
        foreach (var state in fsm.FsmStates)
        {
            if (isStep(state.Name))
                fsm.FsmInject(state.Name, () => OnLocalJack(jack, lift), index: 0);
        }
    }

    void OnLocalJack(Transform jack, Transform lift)
    {
        if (!applying && Active)
            objects.ClaimLoad(jack, lift);
    }

    // Cash an NPC holds out after a job is there on every machine, and taking it is a click that leads
    // from "Wait button" into the state adding it to the wallet. Taking it is replayed so the cash is
    // gone everywhere, without paying again: the wallet is shared.
    static string PayoutState(PlayMakerFSM fsm) =>
        fsm.gameObject.name.StartsWith("PayMoney") ? Target(fsm, "Wait button", "USE") : null;

    // Bills and order forms pay in the state "Check money" leads to when the wallet holds enough.
    static string PaymentState(PlayMakerFSM fsm) => Target(fsm, "Check money", "BUY");

    static string Target(PlayMakerFSM fsm, string from, string eventName)
    {
        var state = fsm.GetState(from);
        if (state == null)
            return null;
        foreach (var transition in state.Transitions)
        {
            if (transition.EventName == eventName)
                return transition.ToState;
        }
        return null;
    }

    // A paid bill tells the system that cuts the power or the phone. The paper itself is only open on
    // the payer's screen, so on the others those messages are sent directly instead of replaying it.
    void HookPayment(PlayMakerFSM fsm, string state)
    {
        var sends = new List<KeyValuePair<PlayMakerFSM, string>>();
        foreach (var action in fsm.GetState(state).Actions)
        {
            if (action is not SendEvent send || send.eventTarget.target != FsmEventTarget.EventTarget.GameObjectFSM)
                continue;
            var target = FindFsm(send.eventTarget.gameObject.GameObject.Value, send.eventTarget.fsmName.Value);
            if (target != null)
                sends.Add(new KeyValuePair<PlayMakerFSM, string>(target, send.sendEvent.Name));
        }
        payments[fsm] = sends;
        HookSteps(fsm, s => s == state);
    }

    static PlayMakerFSM FindFsm(GameObject go, string name)
    {
        if (go == null)
            return null;
        foreach (var fsm in go.GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName == name)
                return fsm;
        }
        return null;
    }

    // Opening a delivered box takes the part out, activates it and destroys the box.
    static bool IsShopBox(PlayMakerFSM fsm) => fsm.transform.parent != null && fsm.transform.parent.name == "Boxes";

    static bool IsBoxOpenStep(string state) => state == "State 1" || state == "State 2";

    // Eaten food and emptied bottles are not destroyed: these states strip the item and move it away.
    static bool IsConsumedStep(string state) => state == "Destroy" || state == "Is garbage";

    // One scroll step of a window crank moves the glass a fixed amount.
    static bool IsWindowStep(string state) => state == "State 1" || state == "State 2";

    // Turn signals blink on their own once switched on, until they are switched off again.
    static bool IsTurnSignalStep(string state) => state == "Activate dash" || state == "Activate dash 2" || state == "State 3";

    // Loose parts break off on a random roll during crashes, which only the driver's machine simulates.
    static bool IsBreakOff(string state) => state == "Break off";

    void HookSteps(PlayMakerFSM fsm, System.Func<string, bool> isStep)
    {
        var states = fsm.FsmStates;
        var hooked = false;
        for (var i = 0; i < states.Length; i++)
        {
            var name = states[i].Name;
            if (!isStep(name))
                continue;
            var index = i;
            fsm.FsmInject(name, () => OnLocalStep(fsm, index), index: 0);
            fsm.AddSharedGlobalTransition(StepEvent + name, name);
            hooked = true;
        }
        if (!hooked)
            unsupported++;
    }

    void OnLocalStep(PlayMakerFSM fsm, int stateIndex)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        var w = session.BeginMessage(MessageType.StateStep);
        w.WriteUInt(id);
        w.WriteByte((byte)stateIndex);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
    }

    void ApplyStep(uint id, int stateIndex)
    {
        var fsm = registry.FindFsm(id);
        if (fsm == null || stateIndex >= fsm.FsmStates.Length)
        {
            MarkUnknown("ApplyStep", id);
            return;
        }
        if (payments.TryGetValue(fsm, out var sends))
        {
            Applied++;
            foreach (var send in sends)
                send.Key.SendEvent(send.Value);
            return;
        }
        Apply(fsm, StepEvent + fsm.FsmStates[stateIndex].Name);
    }

    void OnLocalAssemble(PlayMakerFSM fsm, FsmGameObject partVar, byte variant)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        // The save also assembles parts while loading; only an assembly from the local hand is news.
        var part = registry.Find(partVar.Value);
        if (part == null || !objects.WasRecentlyHeldLocally(part))
            return;

        var w = session.BeginMessage(MessageType.PartAssemble);
        w.WriteUInt(id);
        w.WriteByte(variant);
        w.WriteUInt(part.Id);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
        objects.ReleaseWithoutPose(part);
        objects.RefreshBody(part);
    }

    void OnLocalRemove(PlayMakerFSM fsm, FsmBool detach)
    {
        // Detach is set when a removal cascades from another part; every machine runs that cascade itself.
        if (applying || !Active || detach.Value || !registry.TryGetFsmId(fsm, out var id))
            return;
        session.BeginMessage(MessageType.PartRemove).WriteUInt(id);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
        var part = registry.Find(fsm.gameObject);
        if (part != null)
            objects.ClaimNextStep(part);
    }

    void OnLocalTurn(PlayMakerFSM fsm)
    {
        if (!applying && Active)
            turning.Add(fsm);
    }

    void OnStageSettled(PlayMakerFSM fsm)
    {
        if (!turning.Remove(fsm) || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        var bolt = bolts[fsm];
        var w = session.BeginMessage(MessageType.BoltStage);
        w.WriteUInt(id);
        w.Write((uint)Mathf.Clamp(bolt.Stage.Value, 0, 8), 4);
        w.WriteFloat(bolt.Tightness == null ? 0 : bolt.Tightness.Value);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        switch (type)
        {
            case MessageType.PartAssemble:
                var assemblyId = r.ReadUInt();
                var variant = r.ReadByte();
                var partId = r.ReadUInt();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(assemblyId);
                    w.WriteByte(variant);
                    w.WriteUInt(partId);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyAssemble(assemblyId, variant, partId);
                break;
            case MessageType.PartRemove:
                var removalId = r.ReadUInt();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    session.BeginMessage(type).WriteUInt(removalId);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyRemove(removalId);
                break;
            case MessageType.StateStep:
                var turnId = r.ReadUInt();
                var turnState = r.ReadByte();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(turnId);
                    w.WriteByte(turnState);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyStep(turnId, turnState);
                break;
            case MessageType.ToggleState:
                var toggleId = r.ReadUInt();
                var toggleIndex = r.ReadByte();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(toggleId);
                    w.WriteByte(toggleIndex);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyToggle(toggleId, toggleIndex);
                break;
            case MessageType.BoltStage:
                var boltId = r.ReadUInt();
                var stage = (int)r.Read(4);
                var tightness = r.ReadFloat();
                if (r.Overflow || stage > 8 || float.IsNaN(tightness))
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(boltId);
                    w.Write((uint)stage, 4);
                    w.WriteFloat(tightness);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyStage(boltId, stage, tightness);
                break;
        }
    }

    // Clients only talk to the host, so the host forwards every change to the other clients.
    bool ShouldRelay => session.IsHost && session.Connections.Count > 1;

    void ApplyAssemble(uint assemblyId, byte variant, uint partId)
    {
        var fsm = registry.FindFsm(assemblyId);
        var part = registry.Find(partId);
        if (fsm == null || part == null)
        {
            MarkUnknown("ApplyAssemble", assemblyId);
            return;
        }
        objects.TakeOutOfProxy(part);
        fsm.FsmVariables.FindFsmGameObject("Part").Value = part.Go;
        Apply(fsm, variant == 0 ? AssembleEvent : AssembleEvent2);
        objects.RefreshBody(part);
    }

    void ApplyRemove(uint removalId)
    {
        var fsm = registry.FindFsm(removalId);
        if (fsm == null)
        {
            MarkUnknown("ApplyRemove", removalId);
            return;
        }
        Apply(fsm, RemoveEvent);
        var part = registry.Find(fsm.gameObject);
        if (part != null)
            objects.RefreshBody(part);
    }

    void ApplyStage(uint boltId, int stage, float tightness)
    {
        var fsm = registry.FindFsm(boltId);
        if (fsm == null || !bolts.TryGetValue(fsm, out var bolt))
        {
            MarkUnknown("ApplyStage", boltId);
            return;
        }
        bolt.Stage.Value = stage;
        Apply(fsm, StageEvent);
        if (bolt.Tightness != null)
            bolt.Tightness.Value = tightness;
    }

    void MarkUnknown(string what, uint id)
    {
        Unknown++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm unknown " + what + " " + id.ToString("x8"));
    }

    void Apply(PlayMakerFSM fsm, string eventName)
    {
        Applied++;
        if (!deferred.TryGetValue(fsm, out var queue) && IsRunning(fsm))
        {
            Enter(fsm, eventName);
            return;
        }
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm defer " + registry.FsmKey(fsm) + " " + eventName);
        if (deferredCount >= MaxDeferred)
            return;
        if (queue == null)
        {
            queue = new Queue<string>();
            deferred[fsm] = queue;
            deferredFsms.Add(fsm);
        }
        queue.Enqueue(eventName);
        deferredCount++;
    }

    void Enter(PlayMakerFSM fsm, string eventName)
    {
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm apply " + registry.FsmKey(fsm) + " " + eventName + " from " + fsm.ActiveStateName);
        var wallet = walletSteps.Contains(fsm) && money != null ? money.Value : float.NaN;
        applying = true;
        try
        {
            fsm.SendEvent(eventName);
        }
        finally
        {
            applying = false;
            if (!float.IsNaN(wallet))
                money.Value = wallet;
        }
    }
}
