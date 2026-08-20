using System.Diagnostics;
using MelonLoader;

namespace SonsVRMod
{
    /// <summary>
    /// Measures how much of the frame this mod is actually responsible for.
    /// <para>
    /// The distinction that matters is between work and waiting. The blocking call to the compositor
    /// shows up as main thread time exactly like real work does, but it is the frame being paced to
    /// the headset rather than anything worth optimising. Reporting it separately is the difference
    /// between a useful number and a misleading one.
    /// </para>
    /// <para>
    /// Sections are not nested; each Begin must be followed by its End before the next Begin.
    /// </para>
    /// </summary>
    internal static class VRProfiler
    {
        internal enum Section
        {
            /// <summary>Blocking wait on the compositor. Waiting, not work.</summary>
            Poses = 0,
            /// <summary>Reading the OpenVR action state.</summary>
            Input,
            /// <summary>Turning controller state into synthetic keyboard and mouse events.</summary>
            Keyboard,
            /// <summary>Placing the rig, the head and the game camera.</summary>
            Rig,
            /// <summary>Handing the eye textures to the compositor.</summary>
            Submit,
            Count
        }

        private const int ReportInterval = 300;

        private static readonly double[] Accumulated = new double[(int)Section.Count];
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static long _sectionStart;
        private static long _frameStart;
        private static double _frameTotal;
        private static double _frameTime;
        private static int _frames;

        public static void Begin()
        {
            if (VRConfig.Profile)
                _sectionStart = Watch.ElapsedTicks;
        }

        public static void End(Section section)
        {
            if (VRConfig.Profile)
                Accumulated[(int)section] += (Watch.ElapsedTicks - _sectionStart) * TicksToMs;
        }

        public static void BeginFrame()
        {
            if (VRConfig.Profile)
                _frameStart = Watch.ElapsedTicks;
        }

        /// <summary>
        /// Closes the frame and, every so often, reports the average cost of each section.
        /// </summary>
        public static void EndFrame()
        {
            if (!VRConfig.Profile)
                return;

            _frameTotal += (Watch.ElapsedTicks - _frameStart) * TicksToMs;
            _frameTime += UnityEngine.Time.unscaledDeltaTime * 1000f;

            if (++_frames < ReportInterval)
                return;

            double poses = Accumulated[(int)Section.Poses] / _frames;
            double input = Accumulated[(int)Section.Input] / _frames;
            double keyboard = Accumulated[(int)Section.Keyboard] / _frames;
            double rig = Accumulated[(int)Section.Rig] / _frames;
            double submit = Accumulated[(int)Section.Submit] / _frames;

            double work = input + keyboard + rig + submit;

            double frame = _frameTime / _frames;

            // The span is measured from the start of our update hook to the end of our late update
            // hook, so it contains the whole game update in between. It is not our cost, and saying
            // so avoids reading it as though it were.
            MelonLogger.Msg($"[VRProfiler] per frame over {_frames}: " +
                            $"mod work {work:F2} ms (input {input:F2}, keyboard {keyboard:F2}, " +
                            $"rig {rig:F2}, submit {submit:F2}), " +
                            $"compositor wait {poses:F2} ms, " +
                            $"update to late update span {_frameTotal / _frames:F2} ms (game included), " +
                            $"frame {frame:F1} ms ({(frame > 0.01 ? 1000.0 / frame : 0):F0} fps)");

            for (int i = 0; i < Accumulated.Length; i++)
                Accumulated[i] = 0.0;

            _frameTotal = 0.0;
            _frameTime = 0.0;
            _frames = 0;
        }
    }
}
