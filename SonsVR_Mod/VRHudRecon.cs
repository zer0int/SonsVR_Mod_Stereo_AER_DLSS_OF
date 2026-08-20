using System;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SonsVRMod
{
    /// <summary>
    /// Writes out how the game builds its heads up display, once.
    /// <para>
    /// The mod places the map and the ring around it by moving two canvases that it did not build,
    /// with offsets that were found by eye. That works at one size and comes apart at any other,
    /// and no amount of reasoning from the outside settles which object hangs where: the layout has
    /// to be read off the running game.
    /// </para>
    /// </summary>
    internal static class VRHudRecon
    {
        private static bool _done;

        /// <summary>Dumps the interface tree under the given root, the first time it is called.</summary>
        public static void DumpOnce(Transform root)
        {
            if (_done || root == null)
                return;

            _done = true;

            try
            {
                var report = new StringBuilder();
                report.Append("[VRHudRecon] interface layout under '").Append(root.name).Append("'\n");

                Walk(root, root, 0, report);

                MelonLogger.Msg(report.ToString());
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[VRHudRecon] Could not read the interface layout: {e.Message}");
            }
        }

        private static void Walk(Transform node, Transform root, int depth, StringBuilder report)
        {
            // Deep enough to reach the pieces of the map, shallow enough that the report stays
            // readable: below this the tree is text meshes and mask images.
            if (depth > 5)
                return;

            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                Describe(child, root, depth, report);
                Walk(child, root, depth + 1, report);
            }
        }

        private static void Describe(Transform node, Transform root, int depth, StringBuilder report)
        {
            var rect = node.GetComponent<RectTransform>();

            report.Append(' ', (depth + 1) * 2);
            report.Append(node.name);

            if (!node.gameObject.activeInHierarchy)
                report.Append(" [off]");

            if (node.GetComponent<Canvas>() != null)
                report.Append(" [canvas]");

            var graphic = node.GetComponent<Graphic>();
            if (graphic != null)
                report.Append(" [").Append(graphic.GetIl2CppType().Name).Append(']');

            if (rect != null)
            {
                // Anchors and pivot are what decide which way a thing travels when its canvas is
                // scaled, so they are the point of the whole report.
                report.Append("  pos ").Append(Round(rect.anchoredPosition))
                      .Append(" size ").Append(Round(rect.sizeDelta))
                      .Append(" pivot ").Append(Round(rect.pivot))
                      .Append(" anchors ").Append(Round(rect.anchorMin))
                      .Append('-').Append(Round(rect.anchorMax))
                      .Append(" scale ").Append(rect.localScale.x.ToString("F3"));

                // Where it sits relative to the root of the display, which is the number that has to
                // stay put when the size changes.
                Vector3 local = root.InverseTransformPoint(node.position);
                report.Append(" fromRoot ").Append(Round(new Vector2(local.x, local.y)));
            }

            report.Append('\n');
        }

        private static string Round(Vector2 v)
        {
            return $"({v.x:F1},{v.y:F1})";
        }
    }
}
