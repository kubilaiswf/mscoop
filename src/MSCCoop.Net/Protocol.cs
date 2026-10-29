namespace MSCCoop.Net;

public static class Protocol
{
    public const ushort Magic = 0x4D43;
    public const byte Version = 8;
    public const int HeaderBits = 24;
    public const int MaxPlayers = 4;
    public const int MaxNameChars = 32;
    public const int MaxVersionChars = 16;

    // Legacy Steam P2P drops unreliable packets above 1200 bytes; leave room for its own overhead.
    public const int MaxUnreliableBytes = 1024;
    public const int MaxReliableBytes = 16 * 1024;
}

public enum Channel : byte
{
    Control = 0,
    State = 1,
    Bulk = 2,
}

public enum MessageType : byte
{
    Hello = 1,
    Welcome = 2,
    Reject = 3,
    Disconnect = 4,
    Ping = 5,
    Pong = 6,
    PlayerJoined = 7,
    PlayerLeft = 8,
    FirstGameMessage = 32,
    SaveBegin = 32,
    SaveChunk = 33,
    SaveComplete = 34,
    StartGame = 35,
    Ready = 36,
    PlayerState = 37,
    ObjectHello = 38,
    ObjectSnapshot = 39,
    ObjectClaim = 40,
    ObjectRelease = 41,
    ObjectOwner = 42,
    ObjectStates = 43,
    PartAssemble = 44,
    PartRemove = 45,
    BoltStage = 46,
    WorldTime = 47,
    WorldWeather = 48,
    VehicleStates = 49,
    MoneyDelta = 50,
    MoneyTotal = 51,
    ToggleState = 52,
    SharedDeltas = 53,
    SharedTotals = 54,
    StateStep = 55,
    ObjectGone = 56,
    PhoneRing = 57,
    PhoneAnswer = 58,
    PhoneHangup = 59,
    SpawnItem = 60,
    BagContents = 61,
    HoistAngle = 62,
    WorldWake = 63,
}

public enum RejectReason : byte
{
    ProtocolMismatch = 1,
    ModVersionMismatch = 2,
    GameBuildMismatch = 3,
    Full = 4,
    InProgress = 5,
}
