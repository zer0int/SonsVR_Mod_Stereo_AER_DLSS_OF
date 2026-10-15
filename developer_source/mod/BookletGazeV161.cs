using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppSons.Crafting;
using MelonLoader;
using UnityEngine;

namespace SonsVRMod
{
    /// <summary>
    /// V162 collider-trigger and book-surface repair of V161 native gaze for both held book modes.
    /// Derived from V160 native target/hover evidence and the successful V150 backpack design.
    /// Each Collider is owned by its nearest HeldBookInteraction; nested tabs are distinct targets.
    /// Native action buttons, page changes, and placement are intentionally NOT synthesized.
    /// </summary>
    internal static class BookletGazeV161
    {
        internal static bool Enabled = true; // Set false to restore exact V159 joystick cursor for BOTH books.
        internal static float DwellSeconds = 0.060f; // New target must remain under gaze for this long.
        internal static float ExitDwellSeconds = 0.085f; // Prevent tiny-tab flicker at the boundary.

        private const float MaxDistance = 1.50f;
        private const float CursorFrontOffset = 0.025f; // Cursor must be nearer the HMD than the page collider / paper mesh.
        private const float BookSurfaceRadius = 0.28f; // Only show fallback cursor while the ray is actually over the held book.
        private const float CursorScale = 1.0f;

        private sealed class Target
        {
            internal HeldBookInteraction Item;
            internal readonly List<Collider> Colliders = new List<Collider>();
        }

        private static readonly List<Target> Targets = new List<Target>(72);
        private static Transform _root;
        private static string _mode;
        private static HeldBookInteraction _hovered;
        private static HeldBookInteraction _candidate;
        private static float _candidateSince;
        private static float _nextLog;
        private static int _missingBuilds;
        private static float _nextRetry;
        private static MethodInfo _enter;
        private static MethodInfo _over;
        private static MethodInfo _exit;
        private static bool _reportedCallbacks;
        private static int _errors;

        private static MethodInfo FindMethod(string name)
        {
            Type type = typeof(HeldBookInteraction);
            while (type != null)
            {
                MethodInfo method = type.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null) return method;
                type = type.BaseType;
            }
            return null;
        }

        private static void Invoke(HeldBookInteraction item, MethodInfo method, string label)
        {
            if (item == null || method == null) return;
            try { method.Invoke(item, null); }
            catch (Exception e)
            {
                if (++_errors <= 8)
                    MelonLogger.Warning("[V162 BookGaze] " + label + " " + item.name + ": " +
                        e.GetBaseException().Message);
            }
        }

        private static bool OwnsCollider(HeldBookInteraction item, Collider collider)
        {
            // A parent category's GetComponentsInChildren contains the colliders of all
            // expandable child tabs.  Giving those colliders to the parent makes sub-tabs
            // impossible to select.  Walk upward until the first interaction component.
            if (item == null || collider == null) return false;
            Transform t = collider.transform;
            while (t != null)
            {
                HeldBookInteraction owner = t.GetComponent<HeldBookInteraction>();
                if (owner != null) return owner.GetInstanceID() == item.GetInstanceID();
                t = t.parent;
            }
            return false;
        }

        private static void Build(Transform root, string mode)
        {
            _root = root;
            _mode = mode;
            _missingBuilds++;
            _nextRetry = Time.unscaledTime + 0.45f;
            Targets.Clear();
            if (root == null) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                foreach (HeldBookInteraction item in root.GetComponentsInChildren<HeldBookInteraction>(true))
                {
                    if (item == null) continue;
                    Target target = new Target { Item = item };
                    foreach (Collider col in item.GetComponentsInChildren<Collider>(true))
                    {
                        if (col != null && OwnsCollider(item, col)) target.Colliders.Add(col);
                    }
                    Targets.Add(target);
                }
                MelonLogger.Msg("[V162 BookGaze] CACHE mode=" + mode + " count=" + Targets.Count +
                    " ownColliders=" + CountColliders() + " build=" + _missingBuilds +
                    " ms=" + ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 /
                        System.Diagnostics.Stopwatch.Frequency).ToString("F2"));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V162 BookGaze] CACHE " + e.GetBaseException().Message);
                Targets.Clear();
            }
        }

        private static int CountColliders()
        {
            int count = 0;
            foreach (Target target in Targets) count += target.Colliders.Count;
            return count;
        }

        internal static void Clear()
        {
            if (_root == null && Targets.Count == 0 && _hovered == null) return;
            if (_hovered != null) Invoke(_hovered, _exit, "exit-close");
            // Clean up any native hover left over by the pancake mouse, too.
            foreach (Target target in Targets)
            {
                HeldBookInteraction item = target.Item;
                if (item == null || item == _hovered) continue;
                try { if (item._isMouseOver) Invoke(item, _exit, "exit-close-other"); }
                catch { }
            }
            _root = null;
            _mode = null;
            _hovered = null;
            _candidate = null;
            _missingBuilds = 0;
            Targets.Clear();
        }

        // Visible cursor fallback for blank page regions.  The interaction ray still
        // uses ONLY native owned colliders; this plane is presentation, never a
        // synthetic clickable target.  It follows the animated held book in world
        // coordinates instead of putting the cursor at a fixed head-relative depth.
        private static bool TryBookSurface(Ray ray, string mode, out Vector3 point, out float distance)
        {
            point = Vector3.zero;
            distance = 0f;
            // Page selection cards are on the actual construction page; navigation
            // arrows provide a book-surface reference when tutorial mode has no cards.
            string preferred = mode == "BLUEPRINT" ? "Blueprint0" : "NextPage";
            for (int pass = 0; pass < 3; ++pass)
            {
                foreach (Target target in Targets)
                {
                    HeldBookInteraction item = target.Item;
                    if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy || !item.enabled)
                        continue;
                    string name = item.name;
                    if (pass == 0 && name != preferred) continue;
                    if (pass == 1 && name != "NextPage" && name != "PreviousPage") continue;
                    if (target.Colliders.Count == 0) continue;
                    Vector3 planePoint = item.transform.position;
                    foreach (Collider col in target.Colliders)
                    {
                        if (col != null && col.enabled && col.gameObject.activeInHierarchy)
                        {
                            planePoint = col.bounds.center;
                            break;
                        }
                    }
                    Vector3 normal = item.transform.forward;
                    if (normal.sqrMagnitude < 0.5f) continue;
                    Plane plane = new Plane(normal, planePoint);
                    if (plane.Raycast(ray, out float t) && t > 0.05f && t < MaxDistance)
                    {
                        Vector3 candidate = ray.GetPoint(t);
                        if ((candidate - planePoint).sqrMagnitude <= BookSurfaceRadius * BookSurfaceRadius)
                        {
                            point = candidate - ray.direction * CursorFrontOffset;
                            distance = t;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        internal static void Tick(Transform root, Transform head, GameObject cursor, string mode)
        {
            if (!Enabled || root == null || head == null) { Clear(); return; }
            if (_root != root || _mode != mode)
            {
                Clear();
                if (_enter == null) _enter = FindMethod("OnMouseEnter");
                if (_over == null) _over = FindMethod("OnMouseOver");
                if (_exit == null) _exit = FindMethod("OnMouseExit");
                if (!_reportedCallbacks)
                {
                    _reportedCallbacks = true;
                    MelonLogger.Msg("[V162 BookGaze] native callbacks enter=" + (_enter != null) +
                        " over=" + (_over != null) + " exit=" + (_exit != null));
                }
                Build(root, mode);
            }
            if (Targets.Count == 0 && _missingBuilds < 4 && Time.unscaledTime >= _nextRetry)
                Build(root, mode);

            Ray ray = new Ray(head.position, head.forward);
            HeldBookInteraction best = null;
            Vector3 hitPoint = Vector3.zero;
            float bestDistance = MaxDistance;
            int tested = 0;
            int active = 0;
            int triggerCandidates = 0;
            int disabledColliders = 0;
            int inactiveColliders = 0;
            int misses = 0;
            int hits = 0;
            foreach (Target target in Targets)
            {
                HeldBookInteraction item = target.Item;
                if (item == null || item.gameObject == null || !item.gameObject.activeInHierarchy || !item.enabled) continue;
                active++;
                // Do NOT gate on HeldBookInteraction._isActive: V160 proved it's
                // frequently false before hover, including valid clickable pages.
                foreach (Collider col in target.Colliders)
                {
                    if (col == null) continue;
                    if (!col.gameObject.activeInHierarchy) { ++inactiveColliders; continue; }
                    if (!col.enabled) { ++disabledColliders; continue; }
                    // V162: unlike cooking/backpack physical colliders, booklet UI
                    // selection colliders can be TRIGGERS. Collider.Raycast works on
                    // a trigger when called directly; QueryTriggerInteraction filters
                    // only Physics scene queries. Do not discard these targets.
                    if (col.isTrigger) ++triggerCandidates;
                    tested++;
                    try
                    {
                        if (col.Raycast(ray, out RaycastHit hit, MaxDistance))
                        {
                            ++hits;
                            if (hit.distance > 0f && hit.distance < bestDistance)
                            {
                                best = item;
                                bestDistance = hit.distance;
                                hitPoint = hit.point;
                            }
                        }
                        else ++misses;
                    }
                    catch { ++misses; }
                }
            }

            float now = Time.unscaledTime;
            if (best != _candidate)
            {
                _candidate = best;
                _candidateSince = now;
            }
            float dwell = _candidate == null ? ExitDwellSeconds : DwellSeconds;
            if (best != _hovered && now - _candidateSince >= dwell)
            {
                if (_hovered != null) Invoke(_hovered, _exit, "OnMouseExit");
                _hovered = best;
                if (_hovered != null) Invoke(_hovered, _enter, "OnMouseEnter");
                MelonLogger.Msg("[V162 BookGaze] SWITCH mode=" + mode +
                    " item=" + (_hovered == null ? "none" : _hovered.name + "#" + _hovered.GetInstanceID()) +
                    " distance=" + (best == null ? "-" : bestDistance.ToString("F3")));
            }
            if (_hovered != null) Invoke(_hovered, _over, "OnMouseOver");

            // One native mouse-over at a time.  The desktop virtual mouse can still
            // independently generate Unity OnMouse callbacks; we do NOT move/write it.
            // Like V150's exclusive highlight, reassert the gaze winner after updates.
            foreach (Target target in Targets)
            {
                HeldBookInteraction item = target.Item;
                if (item == null || item == _hovered || item.gameObject == null) continue;
                try { if (item._isMouseOver) Invoke(item, _exit, "OnMouseExit-other"); }
                catch { }
            }

            // V162: use the actual native collider hit, in front of the paper, or
            // intersect the animated booklet page plane for empty-page pointing.
            // If the gaze misses the entire book, HIDE the cursor instead of drawing
            // it behind the book at an arbitrary fixed distance (V161 failure).
            Vector3 cursorPoint = Vector3.zero;
            float surfaceDistance = 0f;
            bool planeFallback = false;
            bool cursorValid = best != null;
            if (cursorValid) cursorPoint = hitPoint - ray.direction * CursorFrontOffset;
            else
            {
                planeFallback = TryBookSurface(ray, mode, out cursorPoint, out surfaceDistance);
                cursorValid = planeFallback;
            }
            if (cursor != null)
            {
                if (cursor.active != cursorValid) cursor.active = cursorValid;
                if (cursorValid)
                {
                    cursor.transform.position = cursorPoint;
                    cursor.transform.rotation = Quaternion.LookRotation(-ray.direction, Vector3.up);
                    cursor.transform.localScale = Vector3.one * CursorScale;
                }
            }
            if (now >= _nextLog)
            {
                _nextLog = now + 0.80f;
                bool over = false;
                try { over = _hovered != null && _hovered._isMouseOver; } catch { }
                MelonLogger.Msg("[V162 BookGaze] TICK mode=" + mode +
                    " gaze=" + (best == null ? "none" : best.name) +
                    " native=" + (_hovered == null ? "none" : _hovered.name) +
                    " over=" + over + " active=" + active + "/" + Targets.Count +
                    " tested=" + tested + " triggers=" + triggerCandidates +
                    " disabled=" + disabledColliders + " inactive=" + inactiveColliders +
                    " hits=" + hits + " misses=" + misses +
                    " cursor=" + (best != null ? "hit" : planeFallback ? "page-plane" : "hidden") +
                    " distance=" + (best == null ? (planeFallback ? surfaceDistance.ToString("F3") : "-") : bestDistance.ToString("F3")));
            }
        }
    }
}
