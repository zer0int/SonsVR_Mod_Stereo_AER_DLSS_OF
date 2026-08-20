using System;
using System.Threading;
using MelonLoader;

namespace SonsVRMod
{
    /// <summary>
    /// Watches the main thread from outside it, and reports where it stopped.
    /// <para>
    /// When the main thread deadlocks nothing on it can report the fact: the log simply ends, which
    /// is all four of the freezes so far looked like. A separate thread keeps running, and can say
    /// both that the main thread has stopped and what it was doing when it did.
    /// </para>
    /// <para>
    /// The stage is a plain field written before and after each call that is known to block. It is
    /// deliberately not synchronised: a torn read is not a concern for a value that only ever holds
    /// one of a handful of constants, and a lock would risk involving the watchdog in the very
    /// deadlock it exists to observe.
    /// </para>
    /// </summary>
    internal static class VRWatchdog
    {
        internal const string Idle = "idle";
        internal const string WaitingForPoses = "WaitGetPoses";
        internal const string SubmittingLeft = "Submit left eye";
        internal const string SubmittingRight = "Submit right eye";
        internal const string DrivingRig = "rig update";
        internal const string MirrorCapture = "mirror copy";
        internal const string HandingOff = "PostPresentHandoff";

        private static volatile string _stage = Idle;
        private static volatile int _heartbeat;
        private static Thread _thread;
        private static bool _reported;

        /// <summary>Marks what the main thread is about to do.</summary>
        public static void Stage(string stage)
        {
            _stage = stage;
        }

        /// <summary>Called once per frame from the main thread to show it is still moving.</summary>
        public static void Beat()
        {
            _heartbeat++;
            _reported = false;
        }

        public static void Start()
        {
            if (_thread != null)
                return;

            _thread = new Thread(Watch)
            {
                IsBackground = true,
                Name = "SonsVR watchdog"
            };

            _thread.Start();
            MelonLogger.Msg("[VRWatchdog] Watching the main thread");
        }

        private static void Watch()
        {
            int lastSeen = -1;
            int stillCount = 0;

            while (true)
            {
                Thread.Sleep(1000);

                int current = _heartbeat;

                if (current != lastSeen)
                {
                    lastSeen = current;
                    stillCount = 0;
                    continue;
                }

                stillCount++;

                // Three seconds without a frame is past any normal hitch, including a scene load,
                // which stalls the frame but not for this long at a stretch.
                if (stillCount >= 3 && !_reported)
                {
                    _reported = true;
                    MelonLogger.Error($"[VRWatchdog] Main thread has not advanced for {stillCount} seconds. " +
                                      $"It stopped in: {_stage}");
                }
            }
        }
    }
}
