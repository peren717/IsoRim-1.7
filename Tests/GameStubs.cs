// Minimal engine stand-ins for headless camera/request lifecycle tests. No rendering is simulated.
using System;
using System.Collections.Generic;

namespace Verse
{
    public enum ProgramState { Entry, Playing }
    public sealed class Game { }
    public sealed class Map { }
    public static class Current
    {
        public static ProgramState ProgramState;
        public static Game Game;
    }
    public static class Find
    {
        public static Map CurrentMap;
        public static Selector Selector = new Selector();
        public static CameraDriver CameraDriver = new CameraDriver();
    }
    public sealed class Selector
    {
        public int NumSelected;
        public object SingleSelectedThing;
    }
    public sealed class CameraDriver { public void JumpToCurrentMapLoc(UnityEngine.Vector3 position) { } }
    public sealed class Pawn
    {
        public Map Map;
        public bool Destroyed, Dead;
        public bool Spawned = true;
        public RaceProperties RaceProps = new RaceProperties();
        public Rotation Rotation = new Rotation();
        public PathFollower pather = new PathFollower();
        public UnityEngine.Vector3 DrawPos;
        public string LabelShort => "test pawn";
    }
    public sealed class RaceProperties { public bool Humanlike = true; }
    public sealed class Rotation { public float AsAngle; }
    public sealed class PathFollower { public bool Moving; }
    public static class Messages { public static void Message(string text, object type, bool historical) { } }
    public static class Log { public static void ErrorOnce(string text, int key) => throw new Exception(text); }
    public static class Translation
    {
        public static string Translate(this string text, params object[] args) => text;
    }
}

namespace RimWorld
{
    public static class MessageTypeDefOf
    {
        public static readonly object RejectInput = new object();
        public static readonly object SilentInput = new object();
    }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3();
        public static Vector3 forward => new Vector3(0, 0, 1);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
    }
    public struct Quaternion
    {
        public static Quaternion Euler(float x, float y, float z) => new Quaternion();
        public static Vector3 operator *(Quaternion rotation, Vector3 v) => v;
    }
    public static class Mathf
    {
        public const float Rad2Deg = 180f / MathF.PI;
        public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
        public static float Repeat(float value, float length) => value - MathF.Floor(value / length) * length;
        public static float DeltaAngle(float a, float b) => Repeat(b - a + 180, 360) - 180;
        public static float Atan2(float a, float b) => MathF.Atan2(a, b);
        public static float Exp(float value) => MathF.Exp(value);
    }
    public static class Time { public static float unscaledDeltaTime = 1f / 60; }
}

namespace IsoRim
{
    public static class View3D { public static bool Active = true; }
    public static class Poses
    {
        public static UnityEngine.Vector3 DrawnSpot(Verse.Pawn pawn, out float headRise)
        {
            headRise = 0;
            return pawn.DrawPos;
        }
    }
    public static class Floors { public static UnityEngine.Vector3 ThingOffset(Verse.Pawn pawn) => UnityEngine.Vector3.zero; }
    public static class IsoRimMod { public static readonly Settings Settings = new Settings(); }
    public sealed class Settings { public float fpEyeHeight = 1; }
    internal static class Cutaway
    {
        internal static readonly List<Verse.Map> Changes = new List<Verse.Map>();
        internal static void WallModeChanged(Verse.Map map) => Changes.Add(map);
    }
}
