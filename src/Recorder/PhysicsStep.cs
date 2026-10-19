using System.Diagnostics;
using System.Linq;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Vigil.Recorder
{
    /// <summary>
    /// Times Unity's own physics step. Each fixed step runs the scripts' FixedUpdate and then the physics
    /// engine (<see cref="FixedUpdate.PhysicsFixedUpdate"/>), which no Harmony hook can reach. Two
    /// markers of Vigil's go into Unity's player loop right before and after that step, so every step's
    /// engine time is known: per frame for the recorder (hitch reports, the timeline's physics_ms) and as
    /// running totals for windows such as the census's. Costs two timestamps a step. When a later change
    /// of the player loop drops the markers, the timer simply stops counting.
    /// </summary>
    internal static class PhysicsStep
    {
        private struct VigilPhysicsStart { }

        private struct VigilPhysicsEnd { }

        private static long s_start, s_frameTicks, s_totalTicks;
        private static int s_frameSteps;
        private static long s_totalSteps;

        /// <summary>True once the markers are in Unity's player loop.</summary>
        public static bool Installed { get; private set; }

        /// <summary>Why the markers could not go in; null when they did or were not tried.</summary>
        public static string Problem { get; private set; }

        /// <summary>Engine physics time and steps since the session began, for windows of time.</summary>
        public static (long Ticks, long Steps) Totals => (s_totalTicks, s_totalSteps);

        /// <summary>Puts the markers around the physics step; false (with <see cref="Problem"/>) when the player loop has no physics step.</summary>
        public static bool Install()
        {
            if (Installed) return true;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var top = loop.subSystemList;
            if (top == null) { Problem = "Unity's player loop has no systems"; return false; }
            for (int i = 0; i < top.Length; i++)
            {
                if (top[i].type != typeof(FixedUpdate) || top[i].subSystemList == null) continue;
                var steps = top[i].subSystemList.ToList();
                if (steps.Any(s => s.type == typeof(VigilPhysicsStart))) { Installed = true; return true; }
                int at = steps.FindIndex(s => s.type == typeof(FixedUpdate.PhysicsFixedUpdate));
                if (at < 0) break;
                steps.Insert(at + 1, new PlayerLoopSystem { type = typeof(VigilPhysicsEnd), updateDelegate = End });
                steps.Insert(at, new PlayerLoopSystem { type = typeof(VigilPhysicsStart), updateDelegate = Start });
                top[i].subSystemList = steps.ToArray();
                loop.subSystemList = top;
                PlayerLoop.SetPlayerLoop(loop);
                Installed = true;
                Problem = null;
                return true;
            }
            Problem = "no physics step found in Unity's fixed update";
            return false;
        }

        /// <summary>Takes the markers out again.</summary>
        public static void Remove()
        {
            if (!Installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            var top = loop.subSystemList;
            for (int i = 0; top != null && i < top.Length; i++)
            {
                if (top[i].subSystemList == null) continue;
                top[i].subSystemList = top[i].subSystemList.Where(s => s.type != typeof(VigilPhysicsStart) && s.type != typeof(VigilPhysicsEnd)).ToArray();
            }
            loop.subSystemList = top;
            PlayerLoop.SetPlayerLoop(loop);
            Installed = false;
        }

        private static void Start() => s_start = Stopwatch.GetTimestamp();

        private static void End()
        {
            if (s_start == 0) return;
            long dt = Stopwatch.GetTimestamp() - s_start;
            s_start = 0;
            s_frameTicks += dt;
            s_frameSteps++;
            s_totalTicks += dt;
            s_totalSteps++;
        }

        /// <summary>The engine's physics time since the last call, in ms, with its steps; starts the next frame's count.</summary>
        public static float TakeFrame(out int steps)
        {
            steps = s_frameSteps;
            float ms = (float)PhaseTimer.TicksToMs(s_frameTicks);
            s_frameTicks = 0;
            s_frameSteps = 0;
            return ms;
        }
    }
}
