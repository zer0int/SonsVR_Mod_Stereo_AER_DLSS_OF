using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppSons.Crafting;
using Il2CppSons.Weapon;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SonsVRMod
{
    /// <summary>
    /// V160: read-only held-book interaction reconnaissance (V159 production baseline). Detects actual held-object
    /// active-state transitions, never assumes D-pad/X delivery or which book opens first.
    /// Does NOT invoke native mouse methods, synthesize input, or reposition any cursor.
    /// Deliberately separate from V150's confirmed backpack gaze code.
    /// </summary>
    internal static class BookletPassiveTraceV151
    {
        internal static bool Enabled = true;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private const float SamplePeriod = 0.05f; // 20Hz native transition polling for tiny tutorial bookmarks
        private const float FullSamplePeriod = 0.25f; // same modest full-pose sampling rate as V151
        private const int MaxObjects = 256;
        private static readonly List<HeldBookInteraction> Items = new List<HeldBookInteraction>(64);
        private static readonly Dictionary<int, string> LastState = new Dictionary<int, string>();
        // V160: genuine native hover transitions, distinct from item/collider activation.
        private static readonly Dictionary<int, bool> LastNativeOver = new Dictionary<int, bool>();
        // Supplemental OpenVR button edges are observation only; page changes/hover are authoritative.
        private static readonly Dictionary<string, bool> LastNativeButtons = new Dictionary<string, bool>();
        private static readonly string[] NativeButtons = { "ButtonLeft_X_Press", "ButtonRight_A_Press", "TriggerRight_Press" };
        private static StreamWriter _writer;
        private static bool _capture;
        private static bool _wasBlueprint;
        private static bool _wasTutorial;
        private static bool _globalAttempted;
        private static bool _blueprintGlobalAttempted;
        private static bool _tutorialGlobalAttempted;
        private static float _inactiveSince = -1f;
        private static float _nextSample;
        private static float _nextFullSample;
        private static float _nextFlush;
        private static float _nextCacheRetry;
        private static float _nextControllerRetry;
        private static float _allowGlobalAfter;
        private static int _session;
        private static int _openCount;
        private static int _samples;
        private static string _path;
        private static BlueprintBookController _blueprintController;
        private static TutorialBookController _tutorialController;

        private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        private static string F(float x) => x.ToString("F4", CI);
        private static string P(Vector3 p) => F(p.x) + "," + F(p.y) + "," + F(p.z);
        private static string PathOf(Transform t)
        {
            if (t == null) return "<null>";
            var names = new List<string>();
            int n = 0;
            while (t != null && n++ < 55)
            {
                names.Add(t.name);
                t = t.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
        private static void Log(string kind, string mode, string details)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine(string.Join("\t", kind, DateTime.Now.ToString("O", CI),
                    F(Time.realtimeSinceStartup), Time.frameCount.ToString(CI), _session.ToString(CI),
                    Csv(mode), Csv(details)));
            }
            catch (Exception e) { MelonLogger.Warning("[V160 BOOK] log failed: " + e.Message); }
        }
        private static void Flush()
        {
            try { _writer?.Flush(); } catch { }
        }
        private static void Begin(bool blueprint, bool tutorial)
        {
            _capture = true;
            _session++;
            _openCount = 1;
            _wasBlueprint = blueprint;
            _wasTutorial = tutorial;
            _inactiveSince = -1f;
            _nextSample = _nextFullSample = _nextFlush = 0f;
            _nextCacheRetry = _nextControllerRetry = 0f;
            _allowGlobalAfter = Time.realtimeSinceStartup + 0.80f;
            _samples = 0;
            _globalAttempted = false;
            _blueprintGlobalAttempted = false; _tutorialGlobalAttempted = false;
            Items.Clear(); LastState.Clear(); LastNativeOver.Clear();
            _blueprintController = null; _tutorialController = null;
            _lastControllerState = "";
            LastNativeButtons.Clear();
            try
            {
                string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "Mods");
                try
                {
                    var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(assemblyPath)) dir = System.IO.Path.GetDirectoryName(assemblyPath);
                }
                catch { }
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "SonsVR_V160_Booklet_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CI) + ".tsv");
                _writer = new StreamWriter(new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false), 65536);
                _writer.WriteLine("kind\twall_time\trealtime_s\tframe\tsession\tmode\tdetails");
                Log("OPEN", Mode(blueprint, tutorial), "V160 on V159; auto armed at first observed book activation (D-pad up); no timeout; native mouseover transitions and tutorial bookmark geometry captured.");
                Flush();
                MelonLogger.Msg("[V160 BOOK] OPEN " + Mode(blueprint,tutorial) + " trace=" + _path);
            }
            catch (Exception e) { MelonLogger.Warning("[V160 BOOK] cannot start trace: " + e); _writer = null; _capture = false; }
        }
        private static string Mode(bool blueprint, bool tutorial) => blueprint && tutorial ? "BOTH_TRANSITION" : blueprint ? "BLUEPRINT" : tutorial ? "TUTORIAL" : "NONE";
        private static void End(string reason)
        {
            if (!_capture) return;
            Log("TRACE_END", Mode(_wasBlueprint, _wasTutorial), "reason=" + reason + " openings=" + _openCount + " samples=" + _samples + " (not proof of selection)");
            Flush();
            try { _writer?.Dispose(); } catch { }
            _writer = null; _capture = false;
            Items.Clear(); LastState.Clear(); LastNativeOver.Clear();
            _blueprintController = null; _tutorialController = null;
            MelonLogger.Msg("[V160 BOOK] TRACE_END reason=" + reason + " trace=" + _path);
        }
        private static void AddItem(HeldBookInteraction item, string origin)
        {
            if (item == null || item.gameObject == null || Items.Count >= MaxObjects) return;
            int id = item.GetInstanceID();
            foreach (HeldBookInteraction existing in Items)
                if (existing != null && existing.GetInstanceID() == id) return;
            Items.Add(item);
            string type = "?"; string pageSlot = "?"; string tab = "?"; string forced = "?";
            try { type = item.LayoutItemType.ToString(); } catch { }
            try { pageSlot = item.PageSlot.ToString(CI); } catch { }
            try { tab = item.GetTabPageIndex().ToString(CI); } catch { }
            try { forced = item.GetForceNewCategory().ToString(); } catch { }
            Log("ITEM", origin, "id="+id+" name="+item.name+" type="+type+" pageSlot="+pageSlot+" tabPage="+tab+
                " forceNewCategory="+forced+" path="+PathOf(item.transform)+" " + Geometry(item));
        }
        private static void ReadLocal(Transform root, string owner)
        {
            if (root == null) return;
            try
            {
                int prev = Items.Count;
                foreach (HeldBookInteraction item in root.GetComponentsInChildren<HeldBookInteraction>(true))
                    AddItem(item, owner+"_LOCAL");
                Log("LOCAL_CACHE", owner, "count="+(Items.Count-prev)+" root="+PathOf(root));
            }
            catch (Exception e) { Log("ERROR", owner, "local items: "+e.GetType().Name+" "+e.Message); }
        }
        private static void FindControllers(Transform blueprintRoot, Transform tutorialRoot, float now)
        {
            if (now < _nextControllerRetry) return;
            _nextControllerRetry = now + 1f;
            if (_blueprintController == null && blueprintRoot != null)
            {
                try
                {
                    _blueprintController = blueprintRoot.GetComponent<BlueprintBookController>() ??
                       blueprintRoot.GetComponentInParent<BlueprintBookController>() ??
                       blueprintRoot.GetComponentInChildren<BlueprintBookController>(true);
                }
                catch (Exception e) { Log("ERROR","BLUEPRINT","controller-local: "+e.Message); }
                if (_blueprintController != null) Log("CONTROLLER", "BLUEPRINT", "local="+PathOf(_blueprintController.transform));
            }
            if (_tutorialController == null && tutorialRoot != null)
            {
                try
                {
                    _tutorialController = tutorialRoot.GetComponent<TutorialBookController>() ??
                      tutorialRoot.GetComponentInParent<TutorialBookController>() ??
                      tutorialRoot.GetComponentInChildren<TutorialBookController>(true);
                }
                catch (Exception e) { Log("ERROR","TUTORIAL","controller-local: "+e.Message); }
                if (_tutorialController != null) Log("CONTROLLER", "TUTORIAL", "local="+PathOf(_tutorialController.transform));
            }
        }
        private static void OneShotGlobalFallback(bool blueprint, bool tutorial)
        {
            if (!_globalAttempted && Items.Count == 0)
            {
                _globalAttempted = true;
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    foreach (HeldBookInteraction item in UnityEngine.Object.FindObjectsOfType<HeldBookInteraction>(true))
                        AddItem(item, "GLOBAL_FALLBACK");
                }
                catch (Exception e) { Log("ERROR", "GLOBAL", "native typed search: "+e.Message); }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Log("GLOBAL_CACHE", Mode(blueprint,tutorial), "count="+Items.Count+" costMs="+ms.ToString("F2",CI));
            }
            if ((blueprint && !_blueprintGlobalAttempted && _blueprintController == null) ||
                (tutorial && !_tutorialGlobalAttempted && _tutorialController == null))
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (blueprint && !_blueprintGlobalAttempted && _blueprintController == null)
                    {
                        _blueprintGlobalAttempted = true;
                        foreach (BlueprintBookController c in UnityEngine.Object.FindObjectsOfType<BlueprintBookController>(true))
                            if (c != null && c.gameObject != null && c.gameObject.activeInHierarchy) { _blueprintController = c; break; }
                    }
                    if (tutorial && !_tutorialGlobalAttempted && _tutorialController == null)
                    {
                        _tutorialGlobalAttempted = true;
                        foreach (TutorialBookController c in UnityEngine.Object.FindObjectsOfType<TutorialBookController>(true))
                            if (c != null && c.gameObject != null && c.gameObject.activeInHierarchy) { _tutorialController = c; break; }
                    }
                }
                catch (Exception e) { Log("ERROR", "GLOBAL", "controller search: "+e.Message); }
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Log("CONTROLLER_SEARCH", Mode(blueprint,tutorial), "blueprint="+(_blueprintController!=null)+" tutorial="+(_tutorialController!=null)+" costMs="+ms.ToString("F2", CI));
            }
        }
        private static string Geometry(HeldBookInteraction item)
        {
            try
            {
                Transform tr = item.transform;
                var sb = new StringBuilder();
                sb.Append("world=").Append(P(tr.position)).Append(" right=").Append(P(tr.right)).Append(" up=").Append(P(tr.up)).Append(" forward=").Append(P(tr.forward));
                RectTransform rt = item.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector3[] corners = new Vector3[4]; rt.GetWorldCorners(corners);
                    sb.Append(" rect=").Append(rt.rect.ToString()).Append(" corners=");
                    foreach (Vector3 v in corners) sb.Append('[').Append(P(v)).Append(']');
                }
                int colliders = 0, renderers = 0;
                foreach (Collider c in item.GetComponentsInChildren<Collider>(true))
                    if (c != null) { if (colliders++ < 4) sb.Append(" collider=").Append(c.name).Append(":").Append(c.bounds.ToString()); }
                foreach (Renderer r in item.GetComponentsInChildren<Renderer>(true))
                    if (r != null) { if (renderers++ < 4) sb.Append(" renderer=").Append(r.name).Append(":").Append(r.bounds.ToString()); }
                sb.Append(" colliderCount=").Append(colliders).Append(" rendererCount=").Append(renderers);
                return sb.ToString();
            }
            catch (Exception e) { return "geometryError="+e.GetType().Name+":"+e.Message; }
        }
        private static string State(HeldBookInteraction item)
        {
            try
            {
                // These are native IL2CPP fields, not GameObject-active approximations.
                return "activeObject="+item.gameObject.activeInHierarchy+" componentEnabled="+item.enabled+
                    " nativeActive="+item._isActive+" nativeOver="+item._isMouseOver;
            }
            catch (Exception e) { return "stateError="+e.GetType().Name+":"+e.Message; }
        }
        private static string ControllerState()
        {
            string a = "NA", b = "NA", p = "NA", t = "NA";
            try { if (_blueprintController != null) a = _blueprintController._currentPageIndex.ToString(CI); } catch { }
            try { if (_tutorialController != null) b = _tutorialController._currentPageIndex.ToString(CI); } catch { }
            try { if (_blueprintController != null) p = _blueprintController._enteringPlacementMode.ToString(); } catch { }
            try { if (_tutorialController != null) t = _tutorialController.gameObject.activeInHierarchy.ToString(); } catch { }
            return "blueprintPage="+a+" tutorialPage="+b+" enteringPlacement="+p+" tutorialControllerActive="+t;
        }
        private static string _lastControllerState = "";
        private static void Inputs()
        {
            // OpenVR digital action rising edges, read-only (not authoritative UI selection).
            // The actual book state and native mouseover/page events take precedence.
            foreach (string key in NativeButtons)
            {
                try
                {
                    bool down = NativeVRInput.GetBoolean(key);
                    bool previous = LastNativeButtons.TryGetValue(key, out bool last) && last;
                    if (down && !previous) Log("INPUT_EDGE", "OPENVR", key);
                    LastNativeButtons[key] = down;
                }
                catch { }
            }
            // InputSystem gamepad is supplemental, not an authoritative VR controller mapping.
            try
            {
                Gamepad pad = Gamepad.current;
                if (pad == null) return;
                if (pad.dpad.up.wasPressedThisFrame) Log("INPUT_EDGE", "GAMEPAD", "DPAD_UP (supplemental)");
                if (pad.buttonWest.wasPressedThisFrame) Log("INPUT_EDGE", "GAMEPAD", "X_BUTTON_WEST (supplemental)");
                if (pad.buttonSouth.wasPressedThisFrame) Log("INPUT_EDGE", "GAMEPAD", "A_BUTTON_SOUTH (supplemental)");
                if (pad.rightTrigger.wasPressedThisFrame) Log("INPUT_EDGE", "GAMEPAD", "RIGHT_TRIGGER (supplemental)");
            }
            catch { }
        }
        // V160: one detailed sample at the exact *observed* native hover transition.
        // Never raycast/write the cursor or invoke mouse lifecycle methods.
        private static string HoverContext(Transform head, GameObject cursor, Mouse virtualMouse, bool blueprint, bool tutorial)
        {
            string headPose = head == null ? "head=NA" : "head="+P(head.position)+" gaze="+P(head.forward);
            string cursorPose = cursor == null ? "cursor=NA" : "cursor="+P(cursor.transform.position)+" cursorActive="+cursor.activeInHierarchy;
            string mouse = "virtualMouse=NA";
            try
            {
                if (virtualMouse != null)
                {
                    Vector2 pos = virtualMouse.position.ReadValue();
                    Vector2 offset = blueprint ? new Vector2(VRPlayer.CampfireCursorOffsetX, VRPlayer.CampfireCursorOffsetY) :
                        new Vector2(VRPlayer.RightHandCursorOffsetX, VRPlayer.RightHandCursorOffsetY);
                    mouse = "virtualMouse="+virtualMouse.name+":"+pos+" visualCast="+(pos+offset)+
                        " screen="+Screen.width+"x"+Screen.height;
                }
            }
            catch { }
            return ControllerState()+" "+headPose+" "+cursorPose+" "+mouse;
        }
        private static void Sample(Transform head, GameObject cursor, Mouse virtualMouse, bool blueprint, bool tutorial, float now)
        {
            if (now < _nextSample) return;
            _nextSample = now + SamplePeriod;
            string mode = Mode(blueprint,tutorial);
            string controller = ControllerState();
            if (controller != _lastControllerState)
            {
                Log("PAGE_OR_CONTROLLER", mode, controller+" prev="+_lastControllerState);
                _lastControllerState = controller;
            }
            string headPose = head == null ? "head=NA" : "head="+P(head.position)+" gaze="+P(head.forward);
            string cursorPose = cursor == null ? "cursor=NA" : "cursor="+P(cursor.transform.position)+" cursorActive="+cursor.activeInHierarchy;
            string over = "none";
            int nativeOverCount=0, activeCount=0;
            foreach (HeldBookInteraction item in Items)
            {
                if (item == null || item.gameObject == null) continue;
                int id = item.GetInstanceID();
                string state = State(item);
                if (item.gameObject.activeInHierarchy) activeCount++;
                bool hasNativeOver = false;
                bool nativeOver = false;
                try
                {
                    nativeOver = item._isMouseOver;
                    hasNativeOver = true;
                    if (nativeOver)
                    {
                        nativeOverCount++;
                        string h = item.name+"#"+id+"@"+P(item.transform.position);
                        over = over == "none" ? h : over+","+h;
                    }
                }
                catch { }
                if (hasNativeOver)
                {
                    // On first discovery only an already-hovered item gets an ENTER event;
                    // never fabricate an EXIT event from an unknown earlier state.
                    bool known = LastNativeOver.TryGetValue(id, out bool wasOver);
                    if (nativeOver != (known && wasOver))
                    {
                        Log(nativeOver ? "HOVER_ENTER" : "HOVER_EXIT", mode,
                            "id="+id+" name="+item.name+" world="+P(item.transform.position)+
                            " active="+item.gameObject.activeInHierarchy+" "+
                            HoverContext(head,cursor,virtualMouse,blueprint,tutorial));
                    }
                    LastNativeOver[id] = nativeOver;
                }
                if (!LastState.TryGetValue(id,out string previous) || previous != state)
                {
                    Log("ITEM_STATE",mode,"id="+id+" name="+item.name+" "+state+" world="+P(item.transform.position)+" prev="+(previous??"<first>"));
                    LastState[id]=state;
                }
            }
            if (now >= _nextFullSample)
            {
                _nextFullSample = now + FullSamplePeriod;
                _samples++;
                string mouse = "virtualMouse=NA";
                try
                {
                    if (virtualMouse != null)
                    {
                        Vector2 pos = virtualMouse.position.ReadValue();
                        Vector2 offset = blueprint ? new Vector2(VRPlayer.CampfireCursorOffsetX, VRPlayer.CampfireCursorOffsetY) :
                            new Vector2(VRPlayer.RightHandCursorOffsetX, VRPlayer.RightHandCursorOffsetY);
                        mouse = "virtualMouse="+virtualMouse.name+":"+pos+" visualCast="+(pos+offset)+" screen="+Screen.width+"x"+Screen.height;
                    }
                }
                catch { }
                Log("SAMPLE",mode,"items="+Items.Count+" active="+activeCount+" overCount="+nativeOverCount+
                    " over="+over+" "+controller+" "+headPose+" "+cursorPose+" "+mouse);
            }
        }
        /// <summary>
        /// One file per running VRPlayer lifetime, NOT one file per opening. Selection often
        /// deactivates the book while blueprint placement begins; X may deactivate both books
        /// during the swap. Neither condition stops this trace, regardless of elapsed time.
        /// When no book is visible Tick does O(1) work and occasionally flushes.
        /// </summary>
        internal static void Tick(Transform blueprintRoot, Transform tutorialRoot, Transform head, GameObject cursor,
                                  Mouse virtualMouse, bool blueprint, bool tutorial)
        {
            if (!Enabled) return;
            float now = Time.realtimeSinceStartup;
            bool anyBook = blueprint || tutorial;
            if (!_capture)
            {
                if (!anyBook) return;
                Begin(blueprint, tutorial);
            }
            if (!anyBook)
            {
                if (_inactiveSince < 0f)
                {
                    _inactiveSince = now;
                    Log("BOOK_HIDDEN", "NONE", "previous=" + Mode(_wasBlueprint, _wasTutorial) +
                        " possible=X_swap, Dpad_close, or native item selection; CAPTURE_CONTINUES");
                    _wasBlueprint = false;
                    _wasTutorial = false;
                    LastNativeButtons.Clear(); // next activation starts fresh button-edge observation
                    Flush();
                }
                // No inactivity timeout, no sampling, and no input polling while books are away.
                if (now >= _nextFlush) { _nextFlush = now + 1f; Flush(); }
                return;
            }
            if (_inactiveSince >= 0f)
            {
                float hiddenDuration = now - _inactiveSince;
                _inactiveSince = -1f;
                ++_openCount;
                Log("BOOK_REAPPEARED", Mode(blueprint, tutorial), "open=" + _openCount +
                    " hiddenSeconds=" + F(hiddenDuration) + " same_trace=true (no time limit)");
                // Native objects may have changed identity since selecting the last blueprint.
                // Re-census locally for each reopening; global rescue stays one-shot per reopen.
                Items.Clear(); LastState.Clear(); LastNativeOver.Clear();
                _blueprintController = null; _tutorialController = null;
                _lastControllerState = "";
                _nextCacheRetry = _nextControllerRetry = 0f;
                _allowGlobalAfter = now + 0.80f;
                _globalAttempted = false;
                _blueprintGlobalAttempted = false;
                _tutorialGlobalAttempted = false;
                _nextSample = _nextFullSample = 0f;
            }
            Inputs();
            if (_wasBlueprint != blueprint || _wasTutorial != tutorial)
            {
                Log("MODE_CHANGE", Mode(blueprint,tutorial), "previous="+Mode(_wasBlueprint,_wasTutorial));
                MelonLogger.Msg("[V160 BOOK] MODE " + Mode(_wasBlueprint,_wasTutorial) + " -> " + Mode(blueprint,tutorial));
                _wasBlueprint=blueprint; _wasTutorial=tutorial;
                _nextCacheRetry = 0f;
            }
            if (now >= _nextCacheRetry)
            {
                _nextCacheRetry = now + (Items.Count==0 ? 0.40f : 3f);
                if (blueprint) ReadLocal(blueprintRoot,"BLUEPRINT");
                if (tutorial) ReadLocal(tutorialRoot,"TUTORIAL");
            }
            FindControllers(blueprintRoot,tutorialRoot,now);
            if (now >= _allowGlobalAfter)
            {
                if (Items.Count == 0 || (blueprint && _blueprintController == null) || (tutorial && _tutorialController == null))
                    OneShotGlobalFallback(blueprint,tutorial);
            }
            Sample(head,cursor,virtualMouse,blueprint,tutorial,now);
            if (now >= _nextFlush) { _nextFlush = now + 1f; Flush(); }
        }
        internal static void Shutdown(string reason) => End(reason);
    }
}
