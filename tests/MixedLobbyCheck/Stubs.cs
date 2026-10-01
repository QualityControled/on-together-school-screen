// Test boundary objects only: no queue, vote, identity, or protocol logic lives here.
using System.Reflection;
using OnTogetherSchoolScreen;

namespace UnityEngine
{
    public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } public static Vector2 zero => new(0, 0); }
    public static class Time { public static float unscaledTime; }
    public static class Mathf
    {
        public static int RoundToInt(float value) => checked((int)MathF.Round(value, MidpointRounding.ToEven));
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        public static float Max(float a, float b) => Math.Max(a, b);
    }
    public class Texture2D { }
    public class Renderer { }
    public class Material { }
    public class GUIStyle { }
    public struct Rect { }
}

namespace PurrNet
{
    public readonly record struct PlayerID(int Value) { public override string ToString() => Value == 0 ? "Server" : Value.ToString("D3"); }
    public struct RPCInfo { public PlayerID sender; }
    public static class NetworkSingleton<T> { public static T I; }
}

public static class MonoSingleton<T> { public static T I; }
public sealed class TextChannelManager
{
    public bool isServer;
    public bool isSpawned = true;
    public string UserName;
    public PurrNet.PlayerID localPlayer;
    public PlayerController MainPlayerController;
}
public sealed class PlayerController { public PurrNet.PlayerID localPlayer; public bool isSpawned = true; }
public sealed class MultiplayerManager { public string LobbyCode; public bool LobbyStatus = true; }
public struct PlayerIDInfo { public byte[] Name; }
public sealed class PlayerPanelController
{
    public List<PurrNet.PlayerID> PlayerIDs = new();
    public List<PlayerIDInfo> IDInfos = new();
    public List<string> PlayerSteamIDs = new();
    public string HostId = "steam:0";
}
public sealed class TestLogger { public void LogWarning(object message) { } }
public sealed class Harmony { }
public readonly record struct Packet(UnityEngine.Vector2 Uv, UnityEngine.Vector2 Previous, int Color, bool Erase, bool BigErase);
public sealed class QuadPainterGPU
{
    public readonly List<Packet> Sent = new();
    public Action<Packet> Deliver;
    public void FillTheBlanksRPC(UnityEngine.Vector2 uv, UnityEngine.Vector2 previous, int color, bool erase, bool bigErase)
    {
        Packet packet = new(uv, previous, color, erase, bigErase);
        Sent.Add(packet);
        Deliver?.Invoke(packet);
    }
}

namespace OnTogetherSchoolScreen
{
    public sealed partial class SchoolScreenPlugin
    {
        private readonly TestLogger Logger = new();
        public readonly List<string> HelperCommands = new();
        private void SendHelper(string command) => HelperCommands.Add(command);
        private void RequestQueueTitle(string id) { }
        private void UpdateLocalVolume(bool force) { }
        public object Invoke(string name, params object[] args) => GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, args);
        public object Read(string name) => GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
        public void Write(string name, object value) => GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, value);
        public static object InvokeStatic(string name, params object[] args) => typeof(SchoolScreenPlugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        public void Activate(TextChannelManager channel, MultiplayerManager lobby)
        {
            _instance = this;
            PurrNet.NetworkSingleton<TextChannelManager>.I = channel;
            MonoSingleton<MultiplayerManager>.I = lobby;
        }
    }
}
