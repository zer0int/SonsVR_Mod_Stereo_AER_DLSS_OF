using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SonsVRMod
{
    /// <summary>
    /// Writes down the whole state of the interface on demand, from a key on the keyboard.
    /// <para>
    /// Everything the mod knows about a menu it learns from a scan that runs on its own schedule and
    /// reports only what it chose, so a menu it never picked up leaves no trace at all: the log shows
    /// three canvases and says nothing about the one actually on screen. Asking for the whole picture
    /// at a moment the player controls — menu open, key pressed — turns "which canvas is the menu"
    /// from something to reason about into something to read.
    /// </para>
    /// <para>
    /// Two keys rather than one because the answer is only useful next to another answer. A canvas
    /// that appears for the pause screen and again for the save screen is part of neither.
    /// </para>
    /// </summary>
    internal static class VRUiDump
    {
        /// <summary>Canvases listed at most, so a scene full of them cannot fill the log.</summary>
        private const int MaxCanvases = 60;

        /// <summary>Children named per level, and how deep the walk goes.</summary>
        private const int MaxChildren = 10;
        private const int MaxDepth = 3;

        private static bool _announced;

        /// <summary>
        /// Reads the two keys. Safe to call every frame: the mod stops writing its own keyboard
        /// events while a menu is up, which is the only time this is meant to be used, so a real
        /// press cannot be overwritten by a synthetic one.
        /// </summary>
        public static void CheckHotkeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (!_announced)
            {
                _announced = true;
                MelonLogger.Msg("[VRUiDump] F9 writes down the interface as the pause menu, " +
                                "F10 as the save menu");
            }

            if (keyboard.f9Key.wasPressedThisFrame)
                Dump("pause menu");
            else if (keyboard.f10Key.wasPressedThisFrame)
                Dump("save menu");
        }

        /// <summary>
        /// Lists every canvas in memory with what it carries and how it is drawn, plus what the
        /// event system is prepared to raycast against.
        /// </summary>
        public static void Dump(string tag)
        {
            try
            {
                MelonLogger.Msg($"[VRUiDump] ================ {tag} ================");

                ReportEventSystem();
                ReportCanvases();
                ReportUiToolkit();

                MelonLogger.Msg($"[VRUiDump] ================ end of {tag} ================");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRUiDump] could not finish: {e}");
            }
        }

        /// <summary>
        /// What would actually receive a click. A canvas can be perfectly placed and perfectly
        /// visible and still take no pointer at all if nothing is prepared to raycast it.
        /// </summary>
        private static void ReportEventSystem()
        {
            EventSystem system = EventSystem.current;

            if (system == null)
            {
                MelonLogger.Msg("[VRUiDump] no event system");
            }
            else
            {
                var module = system.currentInputModule;

                MelonLogger.Msg($"[VRUiDump] event system '{Path(system.transform)}', " +
                                $"enabled {system.isActiveAndEnabled}, " +
                                $"input module {(module != null ? module.GetType().Name : "none")}");
            }

            var raycasters = UnityEngine.Object.FindObjectsOfType<BaseRaycaster>();
            MelonLogger.Msg($"[VRUiDump] {raycasters.Length} raycasters on show");

            foreach (var raycaster in raycasters)
            {
                if (raycaster == null)
                    continue;

                MelonLogger.Msg($"[VRUiDump]   {raycaster.GetType().Name} on '{Path(raycaster.transform)}', " +
                                $"enabled {raycaster.isActiveAndEnabled}");
            }
        }

        private static void ReportCanvases()
        {
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            int shown = 0;
            int active = 0;

            foreach (var canvas in canvases)
            {
                if (canvas != null && canvas.isActiveAndEnabled)
                    active++;
            }

            MelonLogger.Msg($"[VRUiDump] {canvases.Length} canvases in memory, {active} of them on show");

            // On show first. The cap exists so a scene of 247 canvases cannot fill the log, but on
            // the first run it hid the one canvas that mattered behind sixty that did not.
            shown = ListCanvases(canvases, true, shown);
            shown = ListCanvases(canvases, false, shown);

            if (canvases.Length > shown)
                MelonLogger.Msg($"[VRUiDump]   ... {canvases.Length - shown} more not listed");
        }

        private static int ListCanvases(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Canvas> canvases,
                                        bool onShow, int shown)
        {
            foreach (var canvas in canvases)
            {
                if (canvas == null || canvas.isActiveAndEnabled != onShow)
                    continue;

                if (shown >= MaxCanvases)
                    break;

                shown++;
                ReportCanvas(canvas);
            }

            return shown;
        }

        private static void ReportCanvas(Canvas canvas)
        {
            var rect = canvas.GetComponent<RectTransform>();
            Vector2 size = rect != null ? rect.ResolvedSize() : Vector2.zero;

            var raycaster = canvas.GetComponent<GraphicRaycaster>();

            int selectables = canvas.GetComponentsInChildren<Selectable>(true).Length;
            int graphics = 0;
            int hittable = 0;
            int painted = 0;

            foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null)
                    continue;

                graphics++;

                if (graphic.raycastTarget)
                    hittable++;

                // Drawn and not fully transparent. A menu nobody can see and a menu that is not there
                // read the same in a count of components; this is what separates them.
                if (graphic.isActiveAndEnabled && graphic.color.a > 0.01f)
                    painted++;
            }

            MelonLogger.Msg($"[VRUiDump] '{canvas.name}' at '{Path(canvas.transform)}'");
            MelonLogger.Msg($"[VRUiDump]   on show {canvas.isActiveAndEnabled}, " +
                            $"mode {canvas.renderMode}, order {canvas.sortingOrder}, " +
                            $"camera '{(canvas.worldCamera != null ? canvas.worldCamera.name : "none")}', " +
                            $"root {(canvas.isRootCanvas ? "yes" : "no")}, " +
                            $"{size.x:F0}x{size.y:F0} px, scale {canvas.transform.localScale.x:F4}");
            MelonLogger.Msg($"[VRUiDump]   raycaster {(raycaster != null ? (raycaster.enabled ? "yes" : "disabled") : "none")}, " +
                            $"{selectables} selectables, {graphics} graphics, " +
                            $"{hittable} hittable, {painted} painted");

            Walk(canvas.transform, 1);
        }

        /// <summary>
        /// Names what a canvas holds, down a few levels. The counts say whether a canvas could be the
        /// menu; the names say whether it is.
        /// </summary>
        private static void Walk(Transform parent, int depth)
        {
            if (depth > MaxDepth)
                return;

            int count = parent.childCount;
            if (count == 0)
                return;

            string indent = new string(' ', depth * 2);

            for (int i = 0; i < count && i < MaxChildren; i++)
            {
                Transform child = parent.GetChild(i);

                MelonLogger.Msg($"[VRUiDump]   {indent}'{child.name}' " +
                                $"{(child.gameObject.activeInHierarchy ? "on" : "off")}");

                if (child.gameObject.activeInHierarchy)
                    Walk(child, depth + 1);
            }

            if (count > MaxChildren)
                MelonLogger.Msg($"[VRUiDump]   {indent}... {count - MaxChildren} more");
        }

        /// <summary>
        /// Reported even though it has been empty every time, because "we looked and there was
        /// nothing" and "we never looked" are different answers and only one of them is written down.
        /// </summary>
        private static void ReportUiToolkit()
        {
            int documents = 0;

            foreach (var document in UnityEngine.Object.FindObjectsOfType<UnityEngine.UIElements.UIDocument>())
            {
                if (document != null && document.isActiveAndEnabled)
                    documents++;
            }

            var panels = Resources.FindObjectsOfTypeAll<UnityEngine.UIElements.PanelSettings>();

            MelonLogger.Msg($"[VRUiDump] UI Toolkit: {documents} documents on show, " +
                            $"{panels.Length} panel settings in memory");
        }

        private static string Path(Transform transform)
        {
            string path = transform.name;

            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
                path = $"{parent.name}/{path}";

            return path;
        }
    }
}
