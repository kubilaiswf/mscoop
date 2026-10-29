using System.Collections.Generic;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// What a remote copy of a vehicle needs to look and sound driven: its drivetrain and controller are
// stopped and fed the driver's values, since both would otherwise overwrite them from local input.
sealed class VehicleParts(GameObject go)
{
    // Simulated only where the vehicle is driven: elsewhere they would burn fuel a second time and
    // roll their own random engine damage from the driver's rpm.
    static readonly string[] DriverOnlyFsms = { "FuelLine", "RedLining" };

    public readonly Drivetrain Drivetrain = go.GetComponent<Drivetrain>();
    public readonly CarController Controller = go.GetComponent<CarController>();
    public readonly Wheel[] Wheels = go.GetComponent<Axles>() is { } axles ? axles.allWheels : new Wheel[0];
    public readonly PlayMakerFSM Lod = go.GetPlayMaker("LOD");
    public readonly List<NetObject> Attached = new();
    public readonly PlayMakerFSM Starter = FindFsms(go, "Starter") is { Count: > 0 } starters ? starters[0] : null;
    public readonly List<PlayMakerFSM> DriverOnly = FindFsms(go, DriverOnlyFsms);
    public PlayMakerFSM Drive;
    public int EmptySeatChecks;
    public bool RemoteRunning;
    public bool StartSent;
    public bool DrivetrainWasEnabled;
    public bool ControllerWasEnabled;
    public bool HasState;
    public float Rpm;
    public float Throttle;
    public float Brake;
    public float Steering;
    public int Gear;

    static List<PlayMakerFSM> FindFsms(GameObject go, params string[] names)
    {
        var found = new List<PlayMakerFSM>();
        foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            if (System.Array.IndexOf(names, fsm.FsmName) >= 0)
                found.Add(fsm);
        }
        return found;
    }
}

sealed class NetObject(uint id, GameObject go)
{
    public readonly uint Id = id;
    public readonly GameObject Go = go;
    public string Key;
    public readonly int Layer = go.layer;
    public readonly VehicleParts Vehicle = go.GetComponent<CarDynamics>() != null ? new VehicleParts(go) : null;
    public bool IsVehicle => Vehicle != null;
    public readonly bool FixedAnchor = IsFixedAnchor(go);
    public Collider[] SeatTriggers;
    public Rigidbody Body;
    public int NextBodyCheck;
    public OwnerInfo Owner = new() { Owner = Ownership.NoOwner };
    public bool Pending;
    public bool Proxy;
    public bool GameKinematic;
    public RigidbodyInterpolation GameInterpolation;
    public SnapshotBuffer Buffer;
    public int RestSteps;
    public Vector3 LastSentPosition;
    public double LastSentTime = double.NaN;
    public float LastHeldLocally = float.NegativeInfinity;
    public bool DestroyedByNetwork;
    public int NextAttachCheck;
    public NetObject AttachedTo;
    public Vector3 LocalPosition;
    public Quaternion LocalRotation;
    public Vector3 LocalPositionTarget;
    public Quaternion LocalRotationTarget;
    public Vector3 LastSentLocalPosition;
    public Quaternion LastSentLocalRotation;
    public float LastLocalSend = float.NegativeInfinity;
    public bool HasTarget;
    public Vector3 TargetPosition;
    public Quaternion TargetRotation;

    // A kinematic body with no free body above it never moves, like the pivots the garage doors hang on.
    static bool IsFixedAnchor(GameObject go)
    {
        var body = go.GetComponent<Rigidbody>();
        if (body == null || !body.isKinematic || go.GetComponent<CarDynamics>() != null)
            return false;
        for (var t = go.transform.parent; t != null; t = t.parent)
        {
            var above = t.GetComponent<Rigidbody>();
            if (above != null && !above.isKinematic)
                return false;
        }
        return true;
    }
}

// Identifies every synced rigidbody the same way on every machine. Scene objects are keyed by their
// hierarchy path as it is right after the level loads, before any FSM renames or reparents them;
// objects the game spawns while loading the save are keyed by the item id the save gave them.
sealed class ObjectRegistry
{
    // Moved by their own logic on every machine, so each machine would keep claiming them.
    static readonly string[] ExcludedRoots = { "PLAYER", "TRAFFIC", "NPC_CARS", "HUMANS", "RALLY", "TRAIN" };
    public static readonly string[] TrackedFsms = { "Assembly", "Removal", "Screw", "Use", "Data", "Assemble", "BoltCheck", "Horn", "Knob", "Usage", "Buy", "UseNew" };

    readonly List<KeyValuePair<string, GameObject>> awake = new();
    readonly List<KeyValuePair<string, PlayMakerFSM>> awakeFsms = new();
    readonly Dictionary<uint, NetObject> byId = new();
    readonly Dictionary<GameObject, NetObject> byObject = new();
    readonly Dictionary<uint, PlayMakerFSM> fsmsById = new();
    readonly Dictionary<PlayMakerFSM, uint> idsByFsm = new();
    readonly Dictionary<PlayMakerFSM, string> keysByFsm = new();
    readonly Dictionary<GameObject, string> spawned = new();
    int boltsLayer;
    bool built;

    public readonly List<NetObject> Objects = new();
    public readonly List<PlayMakerFSM> Fsms = new();
    public uint Hash { get; private set; }
    public uint FsmHash { get; private set; }

    public void Clear()
    {
        awake.Clear();
        awakeFsms.Clear();
        byId.Clear();
        byObject.Clear();
        fsmsById.Clear();
        idsByFsm.Clear();
        keysByFsm.Clear();
        spawned.Clear();
        Objects.Clear();
        Fsms.Clear();
        Hash = 0;
        built = false;
    }

    public bool Has(string key) => byId.ContainsKey(Fnv.Hash32(key));

    // Objects the game's spawners create are named by spawner and number, the same on every
    // machine, so that name identifies them. Before Build they are remembered and keyed there;
    // after it they are added on the spot, with their FSMs, which are returned to be hooked.
    public List<PlayMakerFSM> AddSpawned(string key, GameObject go)
    {
        if (!built)
        {
            spawned[go] = key;
            return null;
        }
        var id = Fnv.Hash32(key);
        if (byId.ContainsKey(id))
            return null;
        if (go.GetComponent<Rigidbody>() != null)
        {
            var obj = new NetObject(id, go) { Key = key };
            Objects.Add(obj);
            byId[id] = obj;
            byObject[go] = obj;
        }
        var added = new List<PlayMakerFSM>();
        var found = new List<KeyValuePair<string, PlayMakerFSM>>();
        CollectSubtree(go.transform, key, found);
        foreach (var pair in found)
        {
            var fsmId = Fnv.Hash32(pair.Key);
            if (fsmsById.ContainsKey(fsmId) || idsByFsm.ContainsKey(pair.Value))
                continue;
            fsmsById[fsmId] = pair.Value;
            idsByFsm[pair.Value] = fsmId;
            keysByFsm[pair.Value] = pair.Key;
            Fsms.Add(pair.Value);
            added.Add(pair.Value);
        }
        return added;
    }

    static void CollectSubtree(Transform t, string path, List<KeyValuePair<string, PlayMakerFSM>> into)
    {
        CollectFsms(t.gameObject, path, into);
        var names = new Dictionary<string, int>();
        for (var i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            names.TryGetValue(child.name, out var n);
            names[child.name] = n + 1;
            CollectSubtree(child, path + "/" + (n == 0 ? child.name : child.name + "[" + n + "]"), into);
        }
    }

    string SpawnedKey(Transform t)
    {
        var suffix = "";
        for (var current = t; current != null; current = current.parent)
        {
            if (spawned.TryGetValue(current.gameObject, out var key))
                return key + suffix;
            suffix = "/" + current.name + suffix;
        }
        return null;
    }

    public NetObject Find(uint id) => byId.TryGetValue(id, out var o) ? o : null;

    public void Remove(NetObject obj)
    {
        Objects.Remove(obj);
        byId.Remove(obj.Id);
        byObject.Remove(obj.Go);
    }

    public PlayMakerFSM FindFsm(uint id) => fsmsById.TryGetValue(id, out var f) ? f : null;

    public bool TryGetFsmId(PlayMakerFSM fsm, out uint id) => idsByFsm.TryGetValue(fsm, out id);

    public string FsmKey(PlayMakerFSM fsm) => keysByFsm.TryGetValue(fsm, out var key) ? key : "";

    public NetObject Find(GameObject go) => go != null && byObject.TryGetValue(go, out var o) ? o : null;

    public void CaptureAwake()
    {
        Clear();
        boltsLayer = LayerMask.NameToLayer("Bolts");
        var roots = new List<Transform>();
        foreach (Transform t in Resources.FindObjectsOfTypeAll(typeof(Transform)))
        {
            if (t.parent == null && t.gameObject.hideFlags == HideFlags.None && !IsAsset(t))
                roots.Add(t);
        }
        roots.Sort((a, b) =>
        {
            var c = a.GetSiblingIndex().CompareTo(b.GetSiblingIndex());
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });
        Walk(roots, "");
    }

    public void Build()
    {
        var entries = new List<KeyValuePair<string, GameObject>>();
        var seen = new HashSet<GameObject>();
        foreach (var pair in awake)
        {
            if (pair.Value != null && seen.Add(pair.Value))
                entries.Add(new KeyValuePair<string, GameObject>("s:" + pair.Key, pair.Value));
        }

        foreach (Rigidbody body in Resources.FindObjectsOfTypeAll(typeof(Rigidbody)))
        {
            var go = body.gameObject;
            if (go.hideFlags != HideFlags.None || seen.Contains(go) || IsAsset(go.transform.root) || IsExcluded(go))
                continue;
            seen.Add(go);
            var itemId = UseId(go);
            var key = SpawnedKey(go.transform) ?? (itemId != null ? "i:" + itemId : "l:" + PathOf(go.transform));
            entries.Add(new KeyValuePair<string, GameObject>(key, go));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var ids = new Dictionary<uint, int>();
        var keys = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            keys[e.Key] = keys.TryGetValue(e.Key, out var k) ? k + 1 : 1;
            var id = Fnv.Hash32(e.Key);
            ids[id] = ids.TryGetValue(id, out var n) ? n + 1 : 1;
        }

        var hash = Fnv.Offset32;
        var dropped = 0;
        foreach (var e in entries)
        {
            var id = Fnv.Hash32(e.Key);
            if (keys[e.Key] > 1 || ids[id] > 1)
            {
                dropped++;
                continue;
            }
            var obj = new NetObject(id, e.Value) { Key = e.Key };
            Objects.Add(obj);
            byId[id] = obj;
            byObject[e.Value] = obj;
            hash = Fnv.Hash32(e.Key, hash);
        }
        Hash = hash;
        awake.Clear();
        BuildFsms();
        spawned.Clear();
        built = true;
        if (dropped > 0)
            ModConsole.Warning("Co-op: " + dropped + " objects have ambiguous ids and will not be synced.");
    }

    void Walk(List<Transform> level, string parentPath, bool excluded = false)
    {
        var names = new Dictionary<string, int>();
        foreach (var t in level)
        {
            names.TryGetValue(t.name, out var n);
            names[t.name] = n + 1;
            var path = (parentPath.Length == 0 ? "" : parentPath + "/") + (n == 0 ? t.name : t.name + "[" + n + "]");
            var inExcluded = excluded || parentPath.Length == 0 && System.Array.IndexOf(ExcludedRoots, t.name) >= 0;
            if (!inExcluded)
            {
                if (t.GetComponent<Rigidbody>() != null && !IsExcluded(t.gameObject))
                    awake.Add(new KeyValuePair<string, GameObject>(path, t.gameObject));
                CollectFsms(t.gameObject, path, awakeFsms);
            }
            // People and races are simulated on every machine, but the cash a farmer or a rally hands
            // over must still be taken only once.
            else if (t.name.StartsWith("PayMoney"))
            {
                CollectFsms(t.gameObject, path, awakeFsms);
            }

            if (t.childCount == 0)
                continue;
            var children = new List<Transform>(t.childCount);
            for (var i = 0; i < t.childCount; i++)
                children.Add(t.GetChild(i));
            Walk(children, path, inExcluded);
        }
    }

    static void CollectFsms(GameObject go, string path, List<KeyValuePair<string, PlayMakerFSM>> into)
    {
        var fsms = go.GetComponents<PlayMakerFSM>();
        for (var i = 0; i < fsms.Length; i++)
        {
            var name = fsms[i].FsmName;
            // Bills and order forms on paper pay through their buttons.
            if (System.Array.IndexOf(TrackedFsms, name) < 0 && !(name == "Button" && path.StartsWith("Sheets/")))
                continue;
            var n = 0;
            for (var j = 0; j < i; j++)
            {
                if (fsms[j].FsmName == name)
                    n++;
            }
            into.Add(new KeyValuePair<string, PlayMakerFSM>(path + "#" + name + (n == 0 ? "" : "[" + n + "]"), fsms[i]));
        }
    }

    void BuildFsms()
    {
        var entries = new List<KeyValuePair<string, PlayMakerFSM>>();
        var seen = new HashSet<PlayMakerFSM>();
        foreach (var pair in awakeFsms)
        {
            if (pair.Value != null && seen.Add(pair.Value))
                entries.Add(new KeyValuePair<string, PlayMakerFSM>("s:" + pair.Key, pair.Value));
        }
        awakeFsms.Clear();

        var late = new HashSet<GameObject>();
        foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
        {
            var go = fsm.gameObject;
            if (seen.Contains(fsm) || go.hideFlags != HideFlags.None || IsAsset(go.transform.root) || !late.Add(go))
                continue;
            var itemId = UseId(go);
            var lateFsms = new List<KeyValuePair<string, PlayMakerFSM>>();
            CollectFsms(go, SpawnedKey(go.transform) ?? (itemId != null ? "i:" + itemId : "l:" + PathOf(go.transform)), lateFsms);
            foreach (var pair in lateFsms)
            {
                if (seen.Add(pair.Value))
                    entries.Add(pair);
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var counts = new Dictionary<uint, int>();
        foreach (var e in entries)
        {
            var id = Fnv.Hash32(e.Key);
            counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
        }
        var hash = Fnv.Offset32;
        foreach (var e in entries)
        {
            var id = Fnv.Hash32(e.Key);
            if (counts[id] > 1)
                continue;
            fsmsById[id] = e.Value;
            idsByFsm[e.Value] = id;
            keysByFsm[e.Value] = e.Key;
            Fsms.Add(e.Value);
            hash = Fnv.Hash32(e.Key, hash);
        }
        FsmHash = hash;
    }

    bool IsExcluded(GameObject go)
    {
        var root = go.transform.root;
        if (System.Array.IndexOf(ExcludedRoots, root.name) >= 0 || root.name.StartsWith("MSCCoop"))
            return true;
        if (go.CompareTag("RAGDOLL") || go.layer == boltsLayer)
            return true;
        return go.transform != root && root.GetComponent<CarDynamics>() != null;
    }

    // Unity 5.0 has no GameObject.scene. A root that is active but not active in hierarchy can only be an asset.
    static bool IsAsset(Transform root) => root.gameObject.activeSelf && !root.gameObject.activeInHierarchy;

    static string PathOf(Transform t)
    {
        var path = "";
        for (var current = t; current != null; current = current.parent)
        {
            var n = 0;
            var parent = current.parent;
            if (parent != null)
            {
                for (var i = 0; i < current.GetSiblingIndex(); i++)
                {
                    if (parent.GetChild(i).name == current.name)
                        n++;
                }
            }
            var segment = n == 0 ? current.name : current.name + "[" + n + "]";
            path = path.Length == 0 ? segment : segment + "/" + path;
        }
        return path;
    }

    static string UseId(GameObject go)
    {
        foreach (var fsm in go.GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName != "Use")
                continue;
            var id = fsm.FsmVariables.FindFsmString("ID");
            if (id != null && !string.IsNullOrEmpty(id.Value))
                return id.Value;
        }
        return null;
    }
}
