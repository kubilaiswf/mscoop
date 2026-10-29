using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Distributed physics authority: whoever moves an object simulates it and streams it, everyone
// else follows a kinematic copy. The host arbitrates ownership; objects nobody owns rest on
// every machine under local physics, which stays in agreement because nothing is moving.
sealed class ObjectSync
{
    const double SendInterval = 0.05;
    const float ClaimSpeed = 0.15f;
    const float ClaimAngularSpeed = 0.5f;
    const float RestSpeed = 0.05f;
    const float RestAngularSpeed = 0.1f;
    const int RestStepsToRelease = 16;
    const int BodyCheckInterval = 50;
    const int MaxPacketsPerSend = 2;
    const int SnapshotChunk = 400;
    const int VehicleSendDivider = 2;
    const float RpmSmoothing = 0.3f;
    const float LoadReach = 0.5f;
    const float PushRange = 10f;
    const float PushMargin = 0.05f;
    const int AttachCheckInterval = 50;
    const int AttachedCountBits = 6;
    const int MaxAttachedPerVehicle = (1 << AttachedCountBits) - 1;
    const float LocalRange = 8f;
    const int LocalBits = 16;
    const float LocalSmoothing = 0.25f;
    const float LocalRefreshSeconds = 1f;
    const string ReanchorEvent = "MSCCOOP_REANCHOR";
    const string StartEngineEvent = "MSCCOOP_START_ENGINE";

    static readonly string[] EngineRunningStates = { "Start engine", "Crank up", "Running", "Motor running", "Start" };

    // The pickup ray ignores the layer the game itself gives an item while it is in a hand.
    const int HeldElsewhereLayer = 16;

    const string HandPath = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand";
    static readonly string[] PickupStates = { "Set pivot", "Set pivot 2", "Tool picked" };
    static readonly string[] DropStates = { "Drop part", "Drop part 2", "Drop tool" };
    const string DriveState = "Player in car";
    // Passing out leaves the driver's seat of most vehicles through its own state, not Create player.
    static readonly string[] LeaveStates = { "Create player", "Create player 2", "Passout" };
    const string LeaveEvent = "MSCCOOP_LEAVE_SEAT";
    const double SeatCheckInterval = 1;

    readonly ObjectRegistry registry = new();
    readonly Ownership table = new();
    readonly List<Connection> pendingSnapshots = new();
    readonly List<KeyValuePair<uint, OwnerInfo>> released = new();
    readonly List<NetObject> claimNextStep = new();
    readonly List<NetObject> attached = new();
    Session session;
    PlayMakerFSM pickUp;
    FsmGameObject pickedObject;
    bool built;
    bool worldReady;
    int fixedStep;
    int sendCursor;
    int sendTick;
    double nextSend;
    double nextSeatCheck;
    FsmString currentVehicle;

    bool Active => session != null && session.State == SessionState.Connected && built;
    byte Local => session.LocalSlot;

    public ObjectRegistry Registry => registry;

    public void Attach(Session newSession)
    {
        session = newSession;
        table.Clear();
        pendingSnapshots.Clear();
        session.MessageReceived += OnMessage;
        session.PlayerLeft += OnPlayerLeft;
    }

    public void Detach()
    {
        foreach (var obj in registry.Objects)
            EndProxy(obj);
        session = null;
        built = false;
    }

    public void OnLevelLoaded()
    {
        built = false;
        worldReady = false;
        attached.Clear();
        if (Application.loadedLevelName == "GAME" && session != null)
            registry.CaptureAwake();
        else
            registry.Clear();
    }

    public void OnSceneCloned()
    {
        if (Application.loadedLevelName == "GAME" && session != null && !built)
            registry.CaptureAwake();
    }

    public void OnGameReady()
    {
        if (session == null)
            return;
        registry.Build();
        built = true;
        HookPickup();
        currentVehicle = PlayMakerGlobals.Instance.Variables.FindFsmString("PlayerCurrentVehicle");
        HookVehicles();
        ModConsole.Print("Co-op: tracking " + registry.Objects.Count + " objects.");

        if (session.IsHost)
        {
            worldReady = true;
            foreach (var c in pendingSnapshots)
                SendSnapshot(c);
            pendingSnapshots.Clear();
            return;
        }
        var w = session.BeginMessage(MessageType.ObjectHello);
        w.WriteUShort((ushort)registry.Objects.Count);
        w.WriteUInt(registry.Hash);
        w.WriteUShort((ushort)registry.Fsms.Count);
        w.WriteUInt(registry.FsmHash);
        session.Send(session.HostConnection, Channel.Control);
    }

    public void FixedUpdate()
    {
        if (!Active)
            return;
        fixedStep++;
        for (var i = 0; i < claimNextStep.Count; i++)
            Claim(claimNextStep[i], held: false);
        claimNextStep.Clear();
        var hostTime = session.HostTime;
        var dt = Time.fixedDeltaTime;

        var objects = registry.Objects;
        for (var i = objects.Count - 1; i >= 0; i--)
        {
            var obj = objects[i];
            if (obj.Go == null)
            {
                Gone(obj);
                continue;
            }
            var body = Body(obj);
            if (!obj.IsVehicle && fixedStep >= obj.NextAttachCheck)
                UpdateAttachment(obj, body);
            if (obj.AttachedTo != null)
                continue;
            if (obj.Proxy)
            {
                ApplyProxy(obj, body, hostTime, dt);
                continue;
            }
            if (body == null || body.isKinematic)
                continue;

            if (obj.Owner.Owner == Local)
            {
                if (!obj.Owner.Held)
                    TrackRest(obj, body);
            }
            else if (!obj.Owner.IsOwned && worldReady && !body.IsSleeping()
                && (body.velocity.sqrMagnitude > ClaimSpeed * ClaimSpeed
                    || body.angularVelocity.sqrMagnitude > ClaimAngularSpeed * ClaimAngularSpeed))
            {
                ClaimMoving(obj);
            }
        }

        for (var i = 0; i < attached.Count; i++)
        {
            var obj = attached[i];
            var body = Body(obj);
            if (body == null)
                continue;
            if (!obj.AttachedTo.Proxy)
            {
                EndProxy(obj);
                continue;
            }
            if (!obj.Proxy)
            {
                CaptureLocalPose(obj);
                BeginProxy(obj);
            }
            FollowVehicle(obj, body);
        }
    }

    // Body panels, doors, seats, the battery and the fuel tank stay rigidbodies after they are
    // installed and are jointed to the car, and an engine block on the hoist is jointed to the hoist.
    // Owned or interpolated apart from what it is jointed to, a joint pulls that body towards a part
    // that lags behind it, so a jointed part moves with whoever owns the body it hangs on.
    void UpdateAttachment(NetObject obj, Rigidbody body)
    {
        obj.NextAttachCheck = fixedStep + AttachCheckInterval;
        // A joint does not move a kinematic body: the pivot the hoist's chain hangs from holds the hook,
        // it does not follow it.
        var gameKinematic = body != null && body.isKinematic && !obj.Proxy && obj.AttachedTo == null;
        var joint = body == null || gameKinematic ? null : obj.Go.GetComponent<Joint>();
        var parent = joint == null || joint.connectedBody == null ? null : registry.Find(joint.connectedBody.gameObject);
        // A door hinged to a fixed pivot swings on its own and is synced like any free body.
        if (parent == obj || parent?.AttachedTo == obj || parent != null && parent.FixedAnchor)
            parent = null;
        if (parent == obj.AttachedTo)
            return;
        if (Diagnostics.Logging)
            Diagnostics.Log("attach " + obj.Id.ToString("x8") + " " + obj.Key + " -> " + (parent == null ? "none" : parent.Key));
        if (obj.AttachedTo != null)
            Unattach(obj);
        if (parent == null)
        {
            EndProxy(obj);
            if (obj.Owner.IsOwned && obj.Owner.Owner != Local)
                BeginProxy(obj);
            return;
        }
        obj.AttachedTo = parent;
        parent.Vehicle?.Attached.Add(obj);
        attached.Add(obj);
        CaptureLocalPose(obj);
        if (obj.Owner.Owner == Local)
            Release(obj, withPose: false);
    }

    void Unattach(NetObject obj)
    {
        obj.AttachedTo.Vehicle?.Attached.Remove(obj);
        attached.Remove(obj);
        obj.AttachedTo = null;
    }

    static void CaptureLocalPose(NetObject obj)
    {
        var vehicle = obj.AttachedTo.Go.transform;
        var inverse = Quaternion.Inverse(vehicle.rotation);
        obj.LocalPosition = obj.LocalPositionTarget = inverse * (obj.Go.transform.position - vehicle.position);
        obj.LocalRotation = obj.LocalRotationTarget = inverse * obj.Go.transform.rotation;
    }

    // Placed from the pose the car is being moved to, not from where it is, so car and part arrive together.
    static void FollowVehicle(NetObject obj, Rigidbody body)
    {
        if (!body.isKinematic)
        {
            obj.GameKinematic = false;
            body.isKinematic = true;
        }
        var vehicle = obj.AttachedTo;
        var position = vehicle.HasTarget ? vehicle.TargetPosition : vehicle.Go.transform.position;
        var rotation = vehicle.HasTarget ? vehicle.TargetRotation : vehicle.Go.transform.rotation;
        obj.LocalPosition = Vector3.Lerp(obj.LocalPosition, obj.LocalPositionTarget, LocalSmoothing);
        obj.LocalRotation = Quaternion.Slerp(obj.LocalRotation, obj.LocalRotationTarget, LocalSmoothing);
        body.MovePosition(position + rotation * obj.LocalPosition);
        body.MoveRotation(rotation * obj.LocalRotation);
    }

    public void Update()
    {
        if (!Active)
            return;
        var hostTime = session.HostTime;
        if (hostTime >= nextSeatCheck)
        {
            nextSeatCheck = hostTime + SeatCheckInterval;
            CheckSeats();
        }
        if (hostTime < nextSend && nextSend - hostTime < 1)
            return;
        nextSend = hostTime + SendInterval;
        SendStates(hostTime);
        if (++sendTick % VehicleSendDivider == 0)
            SendVehicles();
    }

    void SendVehicles()
    {
        BitWriter w = null;
        var countPosition = 0;
        var count = 0;
        foreach (var obj in registry.Objects)
        {
            var v = obj.Vehicle;
            if (v == null || obj.Owner.Owner != Local || !obj.Owner.Held || v.Drivetrain == null || v.Controller == null)
                continue;
            if (w == null)
            {
                w = session.BeginMessage(MessageType.VehicleStates);
                w.Write(Local, ObjectMessages.SlotBits);
                countPosition = w.BitPosition;
                w.Write(0, ObjectMessages.CountBits);
            }
            WriteVehicle(w, obj.Id, v.Drivetrain.rpm, v.Drivetrain.throttle, v.Controller.brake, v.Controller.steering, v.Drivetrain.gear, IsEngineRunning(v));
            WriteAttached(w, obj);
            count++;
        }
        if (w == null)
            return;
        w.Patch(countPosition, (uint)count, ObjectMessages.CountBits);
        session.Broadcast(Channel.State);
    }

    static bool IsEngineRunning(VehicleParts v) =>
        v.Starter != null && System.Array.IndexOf(EngineRunningStates, v.Starter.ActiveStateName) >= 0;

    static void WriteVehicle(BitWriter w, uint id, float rpm, float throttle, float brake, float steering, int gear, bool running)
    {
        w.WriteUInt(id);
        Quantize.WriteRanged(w, rpm, 0, 10000, 14);
        Quantize.WriteRanged(w, throttle, 0, 1, 8);
        Quantize.WriteRanged(w, brake, 0, 1, 8);
        Quantize.WriteRanged(w, steering, -1, 1, 8);
        w.Write((uint)Mathf.Clamp(gear, 0, 15), 4);
        w.WriteBool(running);
    }

    // Doors, hood and boot lid swing on their hinges, so the driver sends where each part sits on
    // the car whenever it moved, and every part now and then in case a packet was lost.
    static void WriteAttached(BitWriter w, NetObject vehicle)
    {
        var countPosition = w.BitPosition;
        w.Write(0, AttachedCountBits);
        var count = 0;
        var t = vehicle.Go.transform;
        var inverse = Quaternion.Inverse(t.rotation);
        var now = Time.time;
        foreach (var part in vehicle.Vehicle.Attached)
        {
            if (count == MaxAttachedPerVehicle)
                break;
            var position = inverse * (part.Go.transform.position - t.position);
            var rotation = inverse * part.Go.transform.rotation;
            if (now - part.LastLocalSend < LocalRefreshSeconds
                && (position - part.LastSentLocalPosition).sqrMagnitude < 1e-6f
                && Quaternion.Angle(rotation, part.LastSentLocalRotation) < 0.25f)
                continue;
            part.LastSentLocalPosition = position;
            part.LastSentLocalRotation = rotation;
            part.LastLocalSend = now;
            WriteLocalPose(w, part.Id, ToNet(position), ToNet(rotation));
            count++;
        }
        w.Patch(countPosition, (uint)count, AttachedCountBits);
    }

    static void WriteLocalPose(BitWriter w, uint id, Vec3 position, Quat rotation)
    {
        w.WriteUInt(id);
        Quantize.WriteVector(w, position, LocalRange, LocalBits);
        Quantize.WriteRotation(w, rotation, ObjectMessages.RotationBits);
    }

    void ReadVehicles(Connection from, BitReader r)
    {
        var owner = (byte)r.Read(ObjectMessages.SlotBits);
        var count = (int)r.Read(ObjectMessages.CountBits);
        if (r.Overflow || session.IsHost && owner != from.Slot)
            return;

        BitWriter relay = null;
        var relayCountPosition = 0;
        var relayCount = 0;
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadUInt();
            var rpm = Quantize.ReadRanged(r, 0, 10000, 14);
            var throttle = Quantize.ReadRanged(r, 0, 1, 8);
            var brake = Quantize.ReadRanged(r, 0, 1, 8);
            var steering = Quantize.ReadRanged(r, -1, 1, 8);
            var gear = (int)r.Read(4);
            var running = r.ReadBool();
            var attachedCount = (int)r.Read(AttachedCountBits);
            if (r.Overflow)
                return;
            var obj = registry.Find(id);
            var valid = obj?.Vehicle != null && obj.Owner.Owner == owner && obj.Proxy;
            if (valid)
            {
                var v = obj.Vehicle;
                v.Rpm = rpm;
                v.Throttle = throttle;
                v.Brake = brake;
                v.Steering = steering;
                v.Gear = gear;
                v.RemoteRunning = running;
                v.HasState = true;
            }

            var relaying = valid && session.IsHost && session.Connections.Count > 1;
            if (relaying)
            {
                if (relay == null)
                {
                    relay = session.BeginMessage(MessageType.VehicleStates);
                    relay.Write(owner, ObjectMessages.SlotBits);
                    relayCountPosition = relay.BitPosition;
                    relay.Write(0, ObjectMessages.CountBits);
                }
                WriteVehicle(relay, id, rpm, throttle, brake, steering, gear, running);
                relay.Write((uint)attachedCount, AttachedCountBits);
                relayCount++;
            }
            for (var j = 0; j < attachedCount; j++)
            {
                var partId = r.ReadUInt();
                var position = Quantize.ReadVector(r, LocalRange, LocalBits);
                var rotation = Quantize.ReadRotation(r, ObjectMessages.RotationBits);
                if (r.Overflow)
                    return;
                if (!valid)
                    continue;
                if (relaying)
                    WriteLocalPose(relay, partId, position, rotation);
                var part = registry.Find(partId);
                if (part?.AttachedTo != obj)
                    continue;
                part.LocalPositionTarget = ToUnity(position);
                part.LocalRotationTarget = ToUnity(rotation);
            }
        }
        if (relay == null)
            return;
        relay.Patch(relayCountPosition, (uint)relayCount, ObjectMessages.CountBits);
        session.Broadcast(Channel.State, from);
    }

    static void ApplyVehicle(VehicleParts v)
    {
        // The game switches these back on itself, e.g. when the engine starts.
        if (v.Drivetrain != null && v.Drivetrain.enabled)
        {
            v.DrivetrainWasEnabled = true;
            v.Drivetrain.enabled = false;
        }
        if (v.Controller != null && v.Controller.enabled)
        {
            v.ControllerWasEnabled = true;
            v.Controller.enabled = false;
        }
        foreach (var fsm in v.DriverOnly)
            fsm.enabled = false;
        if (!v.HasState || v.Drivetrain == null)
            return;
        ApplyEngine(v);
        v.Drivetrain.rpm = Mathf.Lerp(v.Drivetrain.rpm, v.Rpm, RpmSmoothing);
        v.Drivetrain.throttle = v.Throttle;
        if (v.Gear < v.Drivetrain.gearRatios.Length)
            v.Drivetrain.gear = v.Gear;
        if (v.Controller != null)
        {
            v.Controller.brake = v.Brake;
            v.Controller.brakeKey = v.Brake > 0.1f;
            v.Controller.steering = v.Steering;
        }
        foreach (var wheel in v.Wheels)
            wheel.steering = v.Steering;
    }

    // Starting is a random roll on the driver's machine, so a running engine is entered directly
    // through the state that follows a successful start. Stopping needs nothing: the driver's rpm
    // falls below the game's own stall threshold.
    static void ApplyEngine(VehicleParts v)
    {
        if (!v.RemoteRunning)
        {
            v.StartSent = false;
            return;
        }
        if (v.StartSent || v.Starter == null)
            return;
        v.StartSent = true;
        if (!IsEngineRunning(v))
            v.Starter.SendEvent(StartEngineEvent);
    }

    const float RecentlyHeldSeconds = 3f;

    // The click that assembles a part also drops it from the hand, and the assembly state only runs
    // after a short wait, so by then the part is no longer held.
    public bool WasRecentlyHeldLocally(NetObject obj) =>
        obj.Owner.Owner == Local && obj.Owner.Held || Time.time - obj.LastHeldLocally < RecentlyHeldSeconds;

    // Without a pose: an assembled part is placed by the assembly itself, not by its last world pose.
    public void ReleaseWithoutPose(NetObject obj)
    {
        if (obj.Owner.Owner == Local)
            Release(obj, withPose: false);
    }

    // Used before a remote assembly takes the part out of physics on this machine.
    public void TakeOutOfProxy(NetObject obj)
    {
        EndProxy(obj);
        if (!obj.IsVehicle && obj.Go.layer == HeldElsewhereLayer)
            obj.Go.layer = obj.Layer;
    }

    // Assembly destroys a part's rigidbody and removal adds a new one.
    public void RefreshBody(NetObject obj)
    {
        obj.Body = null;
        obj.NextBodyCheck = 0;
        obj.NextAttachCheck = 0;
    }

    public void ClaimNextStep(NetObject obj)
    {
        RefreshBody(obj);
        claimNextStep.Add(obj);
    }

    // A jack and whatever sits on its lift: the body of a jointed part is the vehicle it is fitted to.
    public void ClaimLoad(Transform tool, Transform lift)
    {
        ClaimForUse(registry.Find(tool.gameObject));
        foreach (var collider in Physics.OverlapSphere(lift.position, LoadReach))
        {
            var body = collider.attachedRigidbody;
            var load = body == null ? null : registry.Find(body.gameObject);
            while (load != null && load.AttachedTo != null)
                load = load.AttachedTo;
            ClaimForUse(load);
        }
    }

    // Never takes an object another player is simulating; one this machine already simulates stays
    // claimed while it keeps being used.
    void ClaimForUse(NetObject obj)
    {
        if (obj == null)
            return;
        if (!obj.Owner.IsOwned)
            Claim(obj, held: false);
        else if (obj.Owner.Owner == Local)
            obj.RestSteps = 0;
    }

    // Food that is eaten, bottles that are emptied and parts some assemblies replace with a fixed
    // mesh destroy themselves on the machine where it happened.
    void Gone(NetObject obj)
    {
        if (obj.AttachedTo != null)
            Unattach(obj);
        registry.Remove(obj);
        if (obj.DestroyedByNetwork)
            return;
        if (Diagnostics.Logging)
            Diagnostics.Log("gone " + obj.Id.ToString("x8") + " " + obj.Key);
        session.BeginMessage(MessageType.ObjectGone).WriteUInt(obj.Id);
        session.Broadcast(Channel.Control);
    }

    void HandleGone(Connection from, uint id)
    {
        if (session.IsHost && session.Connections.Count > 1)
        {
            session.BeginMessage(MessageType.ObjectGone).WriteUInt(id);
            session.Broadcast(Channel.Control, from);
        }
        var obj = registry.Find(id);
        if (obj == null || obj.Go == null)
            return;
        obj.DestroyedByNetwork = true;
        ReleaseChildren(obj.Go.transform);
        Object.Destroy(obj.Go);
    }

    // A delivered box holds its part as a child until it is opened. Where the box was destroyed by
    // the network, the part must not go with it: it comes out of the box the way opening releases it.
    void ReleaseChildren(Transform parent)
    {
        for (var i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            if (registry.Find(child.gameObject) == null)
            {
                ReleaseChildren(child);
                continue;
            }
            child.SetParent(null, true);
            child.gameObject.SetActive(true);
        }
    }

    void TrackRest(NetObject obj, Rigidbody body)
    {
        if (body.IsSleeping() || body.velocity.sqrMagnitude < RestSpeed * RestSpeed
            && body.angularVelocity.sqrMagnitude < RestAngularSpeed * RestAngularSpeed)
        {
            if (++obj.RestSteps >= RestStepsToRelease)
                Release(obj, withPose: true);
        }
        else
        {
            obj.RestSteps = 0;
        }
    }

    void Claim(NetObject obj, bool held)
    {
        if (obj.AttachedTo != null)
        {
            UpdateAttachment(obj, Body(obj));
            if (obj.AttachedTo != null)
                return;
        }
        if (Diagnostics.Logging)
            Diagnostics.Log("claim " + obj.Id.ToString("x8") + " " + obj.Key + " held=" + held);
        obj.RestSteps = 0;
        if (session.IsHost)
        {
            table.TryClaim(obj.Id, Local, held, out var info);
            ApplyOwner(obj, info);
            BroadcastOwner(obj.Id, info, null);
            return;
        }
        obj.Owner.Owner = Local;
        obj.Owner.Held = held;
        obj.Pending = true;
        EndProxy(obj);
        var w = session.BeginMessage(MessageType.ObjectClaim);
        w.WriteUInt(obj.Id);
        w.WriteBool(held);
        session.Send(session.HostConnection, Channel.Control);
    }

    // A proxy is moved kinematically, so here it shoves whatever it touches with unlimited force: a
    // garage door it brushes is torn off its hinges on this machine only. What a proxy pushes goes to
    // whoever simulates the proxy, where the push is real.
    void ClaimMoving(NetObject obj)
    {
        var pusher = FindPusher(obj);
        if (pusher == Ownership.NoOwner)
        {
            Claim(obj, held: false);
            return;
        }
        if (!session.IsHost || !table.TryClaim(obj.Id, pusher, false, out var info))
            return;
        ApplyOwner(obj, info);
        BroadcastOwner(obj.Id, info, null);
    }

    byte FindPusher(NetObject obj)
    {
        if (!TryGetBounds(obj.Go, out var bounds))
            return Ownership.NoOwner;
        bounds.Expand(PushMargin);
        var position = obj.Go.transform.position;
        foreach (var other in registry.Objects)
        {
            if (!other.Proxy || other == obj || other.Go == null
                || (other.Go.transform.position - position).sqrMagnitude > PushRange * PushRange)
                continue;
            var root = other;
            while (root.AttachedTo != null)
                root = root.AttachedTo;
            if (root.Owner.IsOwned && root.Owner.Owner != Local && TryGetBounds(other.Go, out var reach) && reach.Intersects(bounds))
                return root.Owner.Owner;
        }
        return Ownership.NoOwner;
    }

    static bool TryGetBounds(GameObject go, out Bounds bounds)
    {
        bounds = default;
        var found = false;
        foreach (var collider in go.GetComponentsInChildren<Collider>())
        {
            if (collider.isTrigger)
                continue;
            if (found)
                bounds.Encapsulate(collider.bounds);
            else
                bounds = collider.bounds;
            found = true;
        }
        return found;
    }

    void Release(NetObject obj, bool withPose)
    {
        if (Diagnostics.Logging)
            Diagnostics.Log("release " + obj.Id.ToString("x8") + " " + obj.Key + " pose=" + withPose);
        obj.RestSteps = 0;
        if (session.IsHost)
        {
            if (table.Release(obj.Id, Local, out var info))
            {
                ApplyOwner(obj, info);
                BroadcastOwner(obj.Id, info, withPose ? obj : null);
            }
            return;
        }
        obj.Owner.Owner = Ownership.NoOwner;
        obj.Owner.Held = false;
        obj.Pending = true;
        var w = session.BeginMessage(MessageType.ObjectRelease);
        w.WriteUInt(obj.Id);
        w.WriteBool(withPose);
        if (withPose)
            ObjectMessages.WritePose(w, ToNet(obj.Go.transform.position), ToNet(obj.Go.transform.rotation));
        session.Send(session.HostConnection, Channel.Control);
    }

    void SendStates(double hostTime)
    {
        var objects = registry.Objects;
        if (objects.Count == 0)
            return;
        var packets = 0;
        var start = sendCursor;
        BitWriter w = null;
        var countPosition = 0;
        var count = 0;
        for (var n = 0; n < objects.Count; n++)
        {
            var index = (start + n) % objects.Count;
            var obj = objects[index];
            if (obj.Owner.Owner != Local || obj.Proxy || obj.AttachedTo != null)
                continue;
            var body = Body(obj);
            if (body == null || !obj.Owner.Held && (body.isKinematic || body.IsSleeping()))
                continue;

            if (w != null && w.BitPosition + ObjectMessages.MaxStateBits > Protocol.MaxUnreliableBytes * 8)
            {
                FinishStates(w, countPosition, count);
                w = null;
                if (++packets == MaxPacketsPerSend)
                {
                    sendCursor = index;
                    return;
                }
            }
            if (w == null)
            {
                w = BeginStates(hostTime, Local, out countPosition);
                count = 0;
            }
            // The game carries some held objects kinematically, the engine block for one, so their
            // pose comes from the transform and their velocity from the previous send.
            var carried = body.isKinematic;
            var position = carried ? obj.Go.transform.position : body.position;
            var velocity = carried
                ? double.IsNaN(obj.LastSentTime) ? Vector3.zero : (position - obj.LastSentPosition) / (float)(hostTime - obj.LastSentTime)
                : body.velocity;
            obj.LastSentPosition = position;
            obj.LastSentTime = hostTime;
            new ObjectState
            {
                Id = obj.Id,
                Position = ToNet(position),
                Rotation = ToNet(carried ? obj.Go.transform.rotation : body.rotation),
                Velocity = ToNet(velocity),
                AngularVelocity = ToNet(carried ? Vector3.zero : body.angularVelocity),
            }.Write(w);
            count++;
        }
        if (w != null)
            FinishStates(w, countPosition, count);
        sendCursor = 0;
    }

    BitWriter BeginStates(double hostTime, byte owner, out int countPosition)
    {
        var w = session.BeginMessage(MessageType.ObjectStates);
        w.WriteUInt((uint)(long)(hostTime * 1000));
        w.Write(owner, ObjectMessages.SlotBits);
        countPosition = w.BitPosition;
        w.Write(0, ObjectMessages.CountBits);
        return w;
    }

    void FinishStates(BitWriter w, int countPosition, int count)
    {
        w.Patch(countPosition, (uint)count, ObjectMessages.CountBits);
        session.Broadcast(Channel.State);
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        switch (type)
        {
            case MessageType.ObjectHello when session.IsHost:
                var count = r.ReadUShort();
                var hash = r.ReadUInt();
                var fsmCount = r.ReadUShort();
                var fsmHash = r.ReadUInt();
                if (!built)
                {
                    pendingSnapshots.Add(from);
                    break;
                }
                if (count != registry.Objects.Count || hash != registry.Hash)
                    ModConsole.Warning("Co-op: player " + from.Slot + " tracks " + count + " objects, host tracks " + registry.Objects.Count + ". Some objects will not sync.");
                if (fsmCount != registry.Fsms.Count || fsmHash != registry.FsmHash)
                    ModConsole.Warning("Co-op: player " + from.Slot + " tracks " + fsmCount + " FSMs, host tracks " + registry.Fsms.Count + ". Some doors and parts will not sync.");
                SendSnapshot(from);
                break;
            case MessageType.ObjectSnapshot when !session.IsHost:
                ReadSnapshot(r);
                break;
            case MessageType.ObjectClaim when session.IsHost:
                HandleClaim(from, r.ReadUInt(), r.ReadBool());
                break;
            case MessageType.ObjectRelease when session.IsHost:
                HandleRelease(from, r);
                break;
            case MessageType.ObjectOwner when !session.IsHost:
                ReadOwner(r);
                break;
            case MessageType.ObjectStates:
                ReadStates(from, r);
                break;
            case MessageType.VehicleStates:
                ReadVehicles(from, r);
                break;
            case MessageType.ObjectGone:
                var goneId = r.ReadUInt();
                if (!r.Overflow)
                    HandleGone(from, goneId);
                break;
        }
    }

    void HandleClaim(Connection from, uint id, bool held)
    {
        var obj = registry.Find(id);
        if (obj == null)
            return;
        table.TryClaim(id, from.Slot, held, out var info);
        ApplyOwner(obj, info);
        BroadcastOwner(id, info, null);
    }

    void HandleRelease(Connection from, BitReader r)
    {
        var id = r.ReadUInt();
        var hasPose = r.ReadBool();
        Vec3 position = default;
        Quat rotation = default;
        if (hasPose)
            ObjectMessages.ReadPose(r, out position, out rotation);
        var obj = registry.Find(id);
        if (r.Overflow || obj == null || !table.Release(id, from.Slot, out var info))
            return;
        ApplyOwner(obj, info);
        if (hasPose)
            SetPose(obj, position, rotation);
        BroadcastOwner(id, info, hasPose ? obj : null);
    }

    void BroadcastOwner(uint id, OwnerInfo info, NetObject withPose)
    {
        var w = session.BeginMessage(MessageType.ObjectOwner);
        ObjectMessages.WriteOwner(w, id, info);
        w.WriteBool(withPose != null);
        if (withPose != null)
            ObjectMessages.WritePose(w, ToNet(withPose.Go.transform.position), ToNet(withPose.Go.transform.rotation));
        session.Broadcast(Channel.Control);
    }

    void ReadOwner(BitReader r)
    {
        var info = ObjectMessages.ReadOwner(r, out var id);
        var hasPose = r.ReadBool();
        Vec3 position = default;
        Quat rotation = default;
        if (hasPose)
            ObjectMessages.ReadPose(r, out position, out rotation);
        var obj = registry.Find(id);
        if (r.Overflow || obj == null)
            return;
        if (!Seq.IsNewer(info.Seq, obj.Owner.Seq) && !(obj.Pending && info.Seq == obj.Owner.Seq))
            return;
        ApplyOwner(obj, info);
        if (hasPose && info.Owner != Local)
            SetPose(obj, position, rotation);
    }

    void ApplyOwner(NetObject obj, OwnerInfo info)
    {
        if (Diagnostics.Logging)
            Diagnostics.Log("owner " + obj.Id.ToString("x8") + " " + obj.Key + " -> " + info.Owner + " seq=" + info.Seq + " held=" + info.Held);
        obj.Owner = info;
        obj.Pending = false;
        obj.RestSteps = 0;
        if (obj.AttachedTo != null)
            return;
        var heldElsewhere = info.IsOwned && info.Owner != Local && info.Held;
        if (obj.SeatTriggers != null)
        {
            foreach (var trigger in obj.SeatTriggers)
                trigger.enabled = !heldElsewhere;
        }
        // Two players got in at the same moment and the other one was given the seat.
        var drive = obj.Vehicle?.Drive;
        if (heldElsewhere && drive != null && drive.ActiveStateName == DriveState)
            drive.SendEvent(LeaveEvent);

        if (info.IsOwned && info.Owner != Local)
        {
            BeginProxy(obj);
            if (!obj.IsVehicle)
                obj.Go.layer = heldElsewhere ? HeldElsewhereLayer : obj.Layer;
            if (pickedObject != null && pickedObject.Value == obj.Go)
                pickUp.SendEvent("PROCEED Drop");
        }
        else
        {
            EndProxy(obj);
            if (!obj.IsVehicle && obj.Go.layer == HeldElsewhereLayer && (pickedObject == null || pickedObject.Value != obj.Go))
                obj.Go.layer = obj.Layer;
        }
    }

    void ReadStates(Connection from, BitReader r)
    {
        var time = r.ReadUInt() / 1000.0;
        var owner = (byte)r.Read(ObjectMessages.SlotBits);
        var count = (int)r.Read(ObjectMessages.CountBits);
        if (r.Overflow || session.IsHost && owner != from.Slot)
            return;

        BitWriter relay = null;
        var relayCountPosition = 0;
        var relayCount = 0;

        for (var i = 0; i < count; i++)
        {
            var state = ObjectState.Read(r);
            if (r.Overflow)
                return;
            var obj = registry.Find(state.Id);
            if (obj == null || obj.Owner.Owner != owner || !obj.Proxy)
                continue;
            obj.Buffer.Add(time, session.HostTime, new MotionState
            {
                Position = state.Position,
                Rotation = state.Rotation,
                Velocity = state.Velocity,
                AngularVelocity = state.AngularVelocity,
            });

            if (!session.IsHost || session.Connections.Count < 2)
                continue;
            if (relay == null)
            {
                relay = BeginStates(time, owner, out relayCountPosition);
                relayCount = 0;
            }
            state.Write(relay);
            relayCount++;
        }

        if (relay != null)
        {
            relay.Patch(relayCountPosition, (uint)relayCount, ObjectMessages.CountBits);
            session.Broadcast(Channel.State, from);
        }
    }

    void SendSnapshot(Connection to)
    {
        var objects = registry.Objects;
        for (var start = 0; start < objects.Count || start == 0; start += SnapshotChunk)
        {
            var end = Mathf.Min(start + SnapshotChunk, objects.Count);
            var w = session.BeginMessage(MessageType.ObjectSnapshot);
            w.WriteBool(end == objects.Count);
            w.WriteUShort((ushort)(end - start));
            for (var i = start; i < end; i++)
            {
                var obj = objects[i];
                ObjectMessages.WriteOwner(w, obj.Id, table.Get(obj.Id));
                ObjectMessages.WritePose(w, ToNet(obj.Go.transform.position), ToNet(obj.Go.transform.rotation));
            }
            session.Send(to, Channel.Bulk);
            if (end == objects.Count)
                break;
        }
    }

    void ReadSnapshot(BitReader r)
    {
        if (Diagnostics.Logging)
            Diagnostics.Log("snapshot chunk");
        var last = r.ReadBool();
        var count = r.ReadUShort();
        for (var i = 0; i < count; i++)
        {
            var info = ObjectMessages.ReadOwner(r, out var id);
            ObjectMessages.ReadPose(r, out var position, out var rotation);
            if (r.Overflow)
                return;
            var obj = registry.Find(id);
            if (obj == null)
                continue;
            ApplyOwner(obj, info);
            SetPose(obj, position, rotation);
        }
        if (last)
        {
            worldReady = true;
            ModConsole.Print("Co-op: world state received.");
        }
    }

    void OnPlayerLeft(PlayerInfo player)
    {
        if (session == null || !session.IsHost)
            return;
        released.Clear();
        table.ReleaseAll(player.Slot, released);
        foreach (var pair in released)
        {
            var obj = registry.Find(pair.Key);
            if (obj == null)
                continue;
            ApplyOwner(obj, pair.Value);
            BroadcastOwner(pair.Key, pair.Value, null);
        }
    }

    void HookPickup()
    {
        var hand = GameObject.Find(HandPath);
        pickUp = hand == null ? null : hand.GetPlayMaker("PickUp");
        if (pickUp == null)
        {
            ModConsole.Error("Co-op: could not find the pickup logic, held items will not sync.");
            return;
        }
        pickedObject = pickUp.FsmVariables.FindFsmGameObject("PickedObject");
        foreach (var state in PickupStates)
            pickUp.FsmInject(state, OnLocalPickup, index: 0);
        foreach (var state in DropStates)
            pickUp.FsmInject(state, OnLocalDrop, index: 0);
    }

    // Entering the driver's seat holds the vehicle; nobody else can drive it until the driver gets out.
    void HookVehicles()
    {
        foreach (var obj in registry.Objects)
        {
            if (!obj.IsVehicle)
                continue;
            obj.Vehicle.Lod?.AddSharedGlobalTransition(ReanchorEvent, "OFF");
            obj.Vehicle.Starter?.AddSharedGlobalTransition(StartEngineEvent, "Start engine");
            foreach (var fsm in obj.Go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "PlayerTrigger" || fsm.gameObject.name != "DriveTrigger")
                    continue;
                var vehicle = obj;
                fsm.FsmInject(DriveState, () => OnDrive(vehicle, true), index: 0);
                foreach (var state in LeaveStates)
                {
                    if (fsm.GetState(state) == null)
                        continue;
                    fsm.FsmInject(state, () => OnDrive(vehicle, false), index: 0);
                    if (obj.Vehicle.Drive == null)
                        fsm.AddSharedGlobalTransition(LeaveEvent, state);
                    obj.Vehicle.Drive = fsm;
                }
                obj.SeatTriggers = fsm.GetComponents<Collider>();
            }
        }
    }

    // A driver who dies or is moved out of the seat by the game never passes the seat's leave state,
    // and would keep the vehicle locked for everyone. Two checks apart, since the seat is claimed a
    // moment before the game names the current vehicle.
    void CheckSeats()
    {
        if (currentVehicle == null)
            return;
        var seated = !string.IsNullOrEmpty(currentVehicle.Value);
        foreach (var obj in registry.Objects)
        {
            var v = obj.Vehicle;
            if (v == null || obj.Owner.Owner != Local || !obj.Owner.Held)
                continue;
            if (seated)
            {
                v.EmptySeatChecks = 0;
                continue;
            }
            if (++v.EmptySeatChecks < 2)
                continue;
            v.EmptySeatChecks = 0;
            Claim(obj, held: false);
        }
    }

    void OnDrive(NetObject vehicle, bool driving)
    {
        if (!Active || vehicle.Owner.Owner == Local && vehicle.Owner.Held == driving)
            return;
        if (driving || vehicle.Owner.Owner == Local)
            Claim(vehicle, held: driving);
    }

    void OnLocalPickup()
    {
        if (!Active)
            return;
        var obj = registry.Find(pickedObject.Value);
        if (Diagnostics.Logging)
            Diagnostics.Log("pickup " + (pickedObject.Value == null ? "null" : pickedObject.Value.name) + (obj == null ? " UNREGISTERED" : " " + obj.Key));
        if (obj == null)
            return;
        obj.LastHeldLocally = Time.time;
        Claim(obj, held: true);
    }

    void OnLocalDrop()
    {
        if (!Active)
            return;
        var obj = registry.Find(pickedObject.Value);
        if (Diagnostics.Logging)
            Diagnostics.Log("drop " + (pickedObject.Value == null ? "null" : pickedObject.Value.name) + (obj == null ? " UNREGISTERED" : " " + obj.Key));
        if (obj == null)
            return;
        obj.LastHeldLocally = Time.time;
        if (obj.Owner.Owner == Local)
            Claim(obj, held: false);
    }

    void ApplyProxy(NetObject obj, Rigidbody body, double hostTime, float dt)
    {
        // The game toggles some bodies itself, e.g. the Satsuma while a tool is in hand.
        if (body != null && !body.isKinematic)
        {
            obj.GameKinematic = false;
            body.isKinematic = true;
        }
        if (obj.Vehicle != null)
            ApplyVehicle(obj.Vehicle);

        var buffer = obj.Buffer;
        if (buffer.Count == 0)
            return;
        buffer.AdvanceDelay(dt);
        if (!buffer.Sample(hostTime - buffer.Delay, out var m))
            return;
        obj.TargetPosition = ToUnity(m.Position);
        obj.TargetRotation = ToUnity(m.Rotation);
        obj.HasTarget = true;
        if (body != null)
        {
            body.MovePosition(obj.TargetPosition);
            body.MoveRotation(obj.TargetRotation);
        }
        else
        {
            obj.Go.transform.position = ToUnity(m.Position);
            obj.Go.transform.rotation = ToUnity(m.Rotation);
        }
    }

    void BeginProxy(NetObject obj)
    {
        if (obj.Proxy)
            return;
        obj.Proxy = true;
        obj.HasTarget = false;
        obj.Buffer ??= new SnapshotBuffer();
        obj.Buffer.Clear();
        var v = obj.Vehicle;
        if (v != null)
        {
            v.HasState = false;
            v.RemoteRunning = false;
            v.StartSent = false;
            if (v.Drivetrain != null)
            {
                v.DrivetrainWasEnabled = v.Drivetrain.enabled;
                v.Drivetrain.enabled = false;
            }
            if (v.Controller != null)
            {
                v.ControllerWasEnabled = v.Controller.enabled;
                v.Controller.enabled = false;
            }
        }
        var body = Body(obj);
        if (body == null)
            return;
        obj.GameKinematic = body.isKinematic;
        obj.GameInterpolation = body.interpolation;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void EndProxy(NetObject obj)
    {
        if (!obj.Proxy)
            return;
        obj.Proxy = false;
        var v = obj.Vehicle;
        if (v != null)
        {
            if (v.Drivetrain != null)
                v.Drivetrain.enabled = v.DrivetrainWasEnabled;
            if (v.Controller != null)
                v.Controller.enabled = v.ControllerWasEnabled;
            foreach (var fsm in v.DriverOnly)
                fsm.enabled = true;
            Reanchor(obj, v);
        }
        var body = Body(obj);
        if (body == null)
            return;
        body.isKinematic = obj.GameKinematic;
        body.interpolation = obj.GameInterpolation;
    }

    // Out of view range the game pins a parked vehicle to the world with a joint at the spot where it
    // left the range. A vehicle someone else drove away from there would be pulled back once it is
    // simulated here again, so the pin is set again where the vehicle is now.
    static void Reanchor(NetObject obj, VehicleParts v)
    {
        if (v.Lod == null || v.Lod.ActiveStateName != "OFF")
            return;
        foreach (var joint in obj.Go.GetComponents<FixedJoint>())
        {
            if (joint.connectedBody == null)
                Object.DestroyImmediate(joint);
        }
        v.Lod.SendEvent(ReanchorEvent);
    }

    // A part without a rigidbody is bolted onto something and placed by its parent; a world pose
    // from another machine would shift it relative to that parent.
    void SetPose(NetObject obj, Vec3 position, Quat rotation)
    {
        var body = Body(obj);
        if (body == null)
            return;
        obj.Go.transform.position = ToUnity(position);
        obj.Go.transform.rotation = ToUnity(rotation);
        if (body.isKinematic)
            return;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.Sleep();
    }

    // The game destroys a part's rigidbody when it is bolted on and adds a new one when it comes off.
    Rigidbody Body(NetObject obj)
    {
        if (obj.Body == null && fixedStep >= obj.NextBodyCheck)
        {
            obj.Body = obj.Go == null ? null : obj.Go.GetComponent<Rigidbody>();
            obj.NextBodyCheck = fixedStep + BodyCheckInterval;
        }
        return obj.Body;
    }

    static Vec3 ToNet(Vector3 v) => new(v.x, v.y, v.z);
    static Quat ToNet(Quaternion q) => new(q.x, q.y, q.z, q.w);
    static Vector3 ToUnity(Vec3 v) => new(v.X, v.Y, v.Z);
    static Quaternion ToUnity(Quat q) => new(q.X, q.Y, q.Z, q.W);
}
