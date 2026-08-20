using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Image = UnityEngine.UI.Image;

namespace SonsVRMod
{
    /// <summary>
    /// A settings panel that lives in the room with the player.
    /// <para>
    /// Built as a world space canvas rather than with immediate mode drawing, which paints onto the
    /// output image and so would be visible on the monitor but not through the lenses. The point of
    /// these settings is that their effect is judged by looking at it, which means the panel has to
    /// be somewhere the player can see while wearing the headset.
    /// </para>
    /// <para>
    /// Values are written straight through as the sliders move, so the change is visible while the
    /// hand is still on it, and committed to the preferences file when the panel closes.
    /// </para>
    /// </summary>
    internal class VRSettingsPanel
    {
        // Sized for reading through a lens rather than on a monitor: a panel that is comfortable on a
        // screen is at the edge of legibility in a headset, where the same text is spread over far
        // fewer pixels.
        // Text is sized as a fraction of the panel, and the panel is fitted to a height in metres, so
        // what decides legibility is how large the letters are relative to the panel rather than how
        // many units tall they are. Comfortable reading through a lens wants around two degrees, and
        // these proportions give roughly that at the default size and distance.
        // Width of one column. The panel holds two of them: with every setting in a single list
        // the panel grew taller than it was wide, which reads badly in a headset, where a tall
        // surface has to be scanned by moving the head rather than the eyes.
        private const float Width = 900f;
        private const float Gutter = 48f;
        private const float PanelWidth = Width * 2f + Gutter;
        private const float RowHeight = 66f;
        private const float HeaderHeight = 58f;
        private const float Margin = 24f;
        private const int LabelSize = 34;
        private const int HeaderSize = 36;
        private const int TitleSize = 46;

        private const float StepButton = 50f;

        private Canvas _canvas;
        private RectTransform _cursorRect;
        private GameObject _cursorObject;
        private Vector2 _cursorPosition;
        private bool _open;
        private bool _lastToggleInput;
        private bool _lastClick;
        private Slider _heldSlider;

        private readonly System.Collections.Generic.List<Row> _rows =
            new System.Collections.Generic.List<Row>();

        private sealed class Row
        {
            public VRConfig.Adjustable Setting;
            public Slider Slider;
            public Toggle Toggle;
            public Text Value;
        }

        /// <summary>True while the panel has the pointer, so nothing else reads the sticks.</summary>
        public bool IsOpen => _open;

        /// <summary>
        /// Same, reachable without the instance, for the input layer that has to stay out of the way
        /// while the panel is being used.
        /// </summary>
        public static bool AnyOpen { get; private set; }

        public void Update(Transform head)
        {
            if (WasToggleRequested())
                SetOpen(!_open, head);

            if (!_open || _canvas == null)
                return;

            VRLaser.UpdateHandSwitch();
            MoveCursor();
            DriveControls();
            RefreshValues();
        }

        /// <summary>
        /// Opening is bound to a key and to both stick clicks at once.
        /// <para>
        /// The controller combination matters more than the key: reaching for a keyboard means taking
        /// the headset off, which defeats a panel whose whole purpose is being adjusted while worn.
        /// Both sticks together is a gesture nothing else in the game uses.
        /// </para>
        /// </summary>
        private bool WasToggleRequested()
        {
            bool keyboard = Keyboard.current != null && Keyboard.current.insertKey.isPressed;

            bool sticks = NativeVRInput.GetBoolean("ThumbstickLeft_Press") &&
                          NativeVRInput.GetBoolean("ThumbstickRight_Press");

            bool pressed = keyboard || sticks;

            if (pressed == _lastToggleInput)
                return false;

            _lastToggleInput = pressed;
            return pressed;
        }

        private void SetOpen(bool open, Transform head)
        {
            _open = open;
            AnyOpen = open;

            if (_canvas == null && open)
                Build();

            if (_canvas != null)
                _canvas.gameObject.SetActive(open);

            if (open)
            {
                Place(head);
                RefreshValues();
                RememberValues();
                MelonLogger.Msg("[VRSettingsPanel] opened");
            }
            else
            {
                _heldSlider = null;
                _laser.Hide();
                MelonLogger.Msg("[VRSettingsPanel] closed");
            }
        }

        /// <summary>Anchored in front of the player, and left there.</summary>
        private void Place(Transform head)
        {
            if (_canvas == null || head == null)
                return;

            Vector3 forward = head.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-4f)
                return;

            forward.Normalize();

            _canvas.transform.SetPositionAndRotation(
                head.position + forward * VRConfig.SettingsPanelDistance,
                Quaternion.LookRotation(forward, Vector3.up));
        }

        private void Build()
        {
            var columns = SplitIntoColumns(out float content);

            // Title, the taller of the two columns, and a row for the buttons.
            float height = Margin * 2f + RowHeight * 2f + content;

            var root = new GameObject("VRSettingsPanel");
            UnityEngine.Object.DontDestroyOnLoad(root);

            _canvas = root.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            root.AddComponent<GraphicRaycaster>();

            var rect = root.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(PanelWidth, height);
            rect.localScale = Vector3.one * (VRConfig.SettingsPanelHeight / height);

            var background = NewChild(root.transform, "Background").AddComponent<Image>();
            background.color = new Color(0.09f, 0.10f, 0.13f, 0.94f);
            Stretch(background.rectTransform);

            AddLabel(root.transform, "SonsVR-Mod by AMP Games", Margin, height - Margin - RowHeight,
                TitleSize, TextAnchor.MiddleCenter, PanelWidth - Margin * 2f);

            // A rule under the title, so the heading reads as a heading rather than as another row.
            var rule = NewChild(root.transform, "Rule").AddComponent<Image>();
            rule.color = new Color(1f, 1f, 1f, 0.28f);
            Anchor(rule.rectTransform, Margin, height - Margin - RowHeight - 6f,
                PanelWidth - Margin * 2f, 2f);

            float top = height - Margin - RowHeight * 2f;

            // A line down the gutter. Two columns of similar looking rows need something between
            // them, or the eye runs across the gap and reads one row as the continuation of another.
            var divider = NewChild(root.transform, "Divider").AddComponent<Image>();
            divider.color = new Color(1f, 1f, 1f, 0.16f);
            Anchor(divider.rectTransform, Width + Gutter * 0.5f, Margin + RowHeight, 2f,
                top - Margin - RowHeight * 0.5f);

            for (int column = 0; column < columns.Count; column++)
            {
                float x = column * (Width + Gutter);
                float y = top;
                string group = null;

                foreach (var setting in columns[column])
                {
                    // A heading whenever the group changes, so a reader can tell at a glance which
                    // values belong together instead of matching them up by name.
                    if (setting.Category != group)
                    {
                        group = setting.Category;
                        AddGroupHeader(root.transform, group, x, y);
                        y -= HeaderHeight;
                    }

                    _rows.Add(setting.IsToggle
                        ? BuildToggleRow(root.transform, setting, x, y)
                        : BuildSliderRow(root.transform, setting, x, y));

                    y -= RowHeight;
                }
            }

            BuildButtons(root.transform, Margin);
            BuildCursor(root.transform);

            MelonLogger.Msg($"[VRSettingsPanel] built with {_rows.Count} settings in two columns");
        }

        /// <summary>
        /// Sorts the settings into the two columns of the panel.
        /// <para>
        /// The column is a property of the setting, chosen where the settings are declared, so the
        /// panel only reads it. Declaration order is kept within a column, which is what makes the
        /// group headings line up with runs of rows.
        /// </para>
        /// </summary>
        private static System.Collections.Generic.List<
            System.Collections.Generic.List<VRConfig.Adjustable>> SplitIntoColumns(out float tallest)
        {
            var columns = new System.Collections.Generic.List<
                System.Collections.Generic.List<VRConfig.Adjustable>>
            {
                new System.Collections.Generic.List<VRConfig.Adjustable>(),
                new System.Collections.Generic.List<VRConfig.Adjustable>()
            };

            foreach (var setting in VRConfig.Adjustables)
                columns[Mathf.Clamp(setting.Column, 0, columns.Count - 1)].Add(setting);

            tallest = Mathf.Max(ColumnHeight(columns[0]), ColumnHeight(columns[1]));
            return columns;
        }

        private static float ColumnHeight(
            System.Collections.Generic.List<VRConfig.Adjustable> column)
        {
            float height = RowHeight * column.Count;
            string group = null;

            foreach (var setting in column)
            {
                if (setting.Category != group)
                {
                    group = setting.Category;
                    height += HeaderHeight;
                }
            }

            return height;
        }

        private void AddGroupHeader(Transform parent, string title, float x, float y)
        {
            AddLabel(parent, title.ToUpperInvariant(), x + Margin, y, HeaderSize, TextAnchor.LowerLeft, 400f)
                .color = new Color(0.55f, 0.78f, 1f, 1f);

            var rule = NewChild(parent, "GroupRule").AddComponent<Image>();
            rule.color = new Color(0.55f, 0.78f, 1f, 0.35f);
            Anchor(rule.rectTransform, x + Margin, y - 2f, Width - Margin * 2f, 1f);
        }

        private Row BuildSliderRow(Transform parent, VRConfig.Adjustable setting, float x, float y)
        {
            AddLabel(parent, setting.Label, x + Margin, y, LabelSize, TextAnchor.MiddleLeft, 360f);

            // One twentieth of the range per press: fine enough to settle on a value, coarse enough
            // to cross the range without wearing out a trigger finger.
            float step = (setting.Max - setting.Min) / 20f;

            AddButton(parent, "-", x + 390f, y + 7f, StepButton, new Color(0.22f, 0.24f, 0.30f, 1f),
                () => Nudge(setting, -step));

            var sliderObject = NewChild(parent, "Slider");
            var sliderRect = sliderObject.GetComponent<RectTransform>();
            Anchor(sliderRect, x + 442f, y + 14f, 260f, 26f);

            // An opaque light track, not a faint tint: on a translucent panel against a forest, a
            // barely tinted control has nothing to read against and disappears into the trees.
            var track = NewChild(sliderObject.transform, "Track").AddComponent<Image>();
            track.color = new Color(0.72f, 0.74f, 0.78f, 1f);
            Stretch(track.rectTransform);

            var fillArea = NewChild(sliderObject.transform, "Fill");
            var fill = fillArea.AddComponent<Image>();
            fill.color = new Color(0.25f, 0.55f, 0.95f, 1f);
            Stretch(fill.rectTransform);

            var slider = sliderObject.AddComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.targetGraphic = track;
            slider.minValue = setting.Min;
            slider.maxValue = setting.Max;
            slider.value = Mathf.Clamp(setting.Read(), setting.Min, setting.Max);

            AddButton(parent, "+", x + 714f, y + 7f, StepButton, new Color(0.22f, 0.24f, 0.30f, 1f),
                () => Nudge(setting, step));

            var value = AddLabel(parent, "", x + 766f, y, LabelSize, TextAnchor.MiddleRight, 110f);

            return new Row { Setting = setting, Slider = slider, Value = value };
        }

        /// <summary>Moves a value by one step and keeps it inside its range.</summary>
        private void Nudge(VRConfig.Adjustable setting, float step)
        {
            float value = Mathf.Clamp(setting.Read() + step, setting.Min, setting.Max);
            setting.Write(value);
            RefreshValues();
        }

        private Row BuildToggleRow(Transform parent, VRConfig.Adjustable setting, float x, float y)
        {
            AddLabel(parent, setting.Label, x + Margin, y, LabelSize, TextAnchor.MiddleLeft, 500f);

            var toggleObject = NewChild(parent, "Toggle");
            Anchor(toggleObject.GetComponent<RectTransform>(), x + 560f, y + 12f, 30f, 30f);

            var box = NewChild(toggleObject.transform, "Box").AddComponent<Image>();
            box.color = new Color(0.72f, 0.74f, 0.78f, 1f);
            Stretch(box.rectTransform);

            var tick = NewChild(toggleObject.transform, "Tick").AddComponent<Image>();
            tick.color = new Color(0.15f, 0.65f, 0.30f, 1f);
            Stretch(tick.rectTransform, 5f);

            var toggle = toggleObject.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = tick;
            toggle.isOn = setting.Read() >= 0.5f;

            return new Row { Setting = setting, Toggle = toggle };
        }

        private sealed class Button
        {
            public RectTransform Rect;
            public Action Action;
        }

        private readonly System.Collections.Generic.List<Button> _buttons =
            new System.Collections.Generic.List<Button>();

        private void BuildButtons(Transform parent, float y)
        {
            // Level with the buttons rather than under the title: the version is something a player
            // reads once, when reporting a problem, so it belongs in the quiet corner of the panel.
            AddLabel(parent, Version(), Margin, y - 4f, LabelSize - 6, TextAnchor.MiddleLeft, 400f)
                .color = new Color(1f, 1f, 1f, 0.45f);

            AddButton(parent, "OK", PanelWidth - Margin - 330f, y, 150f,
                new Color(0.18f, 0.45f, 0.24f, 1f), Accept);

            AddButton(parent, "Cancel", PanelWidth - Margin - 160f, y, 150f,
                new Color(0.45f, 0.20f, 0.20f, 1f), Discard);
        }

        /// <summary>The release this build belongs to, as shown in the corner of the panel.</summary>
        private static string Version()
        {
            return "v" + ModInfo.Version;
        }

        private void AddButton(Transform parent, string label, float x, float y, float width,
            Color colour, Action action)
        {
            var go = NewChild(parent, label);
            var rect = go.GetComponent<RectTransform>();
            Anchor(rect, x, y, width, StepButton);

            // The image goes on the button itself, so its rect is the button's rect: stretching it
            // here would undo the placement just made and let the button fill the whole panel.
            var image = go.AddComponent<Image>();
            image.color = colour;

            var text = AddLabel(go.transform, label, 0f, 0f, LabelSize, TextAnchor.MiddleCenter, width);
            Stretch(text.rectTransform);

            _buttons.Add(new Button { Rect = rect, Action = action });
        }

        /// <summary>Keeps the current values and writes them to the file.</summary>
        private void Accept()
        {
            VRConfig.Save();
            SetOpen(false, null);
        }

        /// <summary>
        /// Puts every value back to what it was when the panel opened.
        /// <para>
        /// Necessary because the settings apply as the slider moves: without a way back, trying a
        /// value means living with it, which makes the panel something to be careful with rather
        /// than something to explore.
        /// </para>
        /// </summary>
        private void Discard()
        {
            for (int i = 0; i < _rows.Count && i < _openingValues.Count; i++)
                _rows[i].Setting.Write(_openingValues[i]);

            RefreshValues();
            SetOpen(false, null);
            MelonLogger.Msg("[VRSettingsPanel] changes discarded");
        }

        private readonly System.Collections.Generic.List<float> _openingValues =
            new System.Collections.Generic.List<float>();

        private void RememberValues()
        {
            _openingValues.Clear();
            foreach (var row in _rows)
                _openingValues.Add(row.Setting.Read());
        }

        private void BuildCursor(Transform parent)
        {
            _cursorObject = NewChild(parent, "Cursor");

            var image = _cursorObject.AddComponent<Image>();
            image.sprite = VRLaser.CircleSprite();
            image.color = new Color(1f, 0.85f, 0.2f, 0.95f);
            image.raycastTarget = false;

            _cursorRect = _cursorObject.GetComponent<RectTransform>();
            _cursorRect.sizeDelta = new Vector2(12f, 12f);
            _cursorRect.anchorMin = new Vector2(0.5f, 0.5f);
            _cursorRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cursorRect.pivot = new Vector2(0.5f, 0.5f);
        }

        private readonly VRLaser _laser = new VRLaser();

        private void MoveCursor()
        {
            // Aiming wins when the beam is on the panel: the hand already knows how to point at a
            // thing, so the cursor goes straight there instead of travelling.
            if (_laser.TryAim(_canvas.GetComponent<RectTransform>(), out Vector2 aimed))
            {
                _cursorPosition = aimed;

                if (_cursorRect != null)
                    _cursorRect.anchoredPosition = _cursorPosition;

                return;
            }

            Vector2 stick = NativeVRInput.GetVector2("ThumbstickRight");
            if (stick.magnitude < 0.15f)
                stick = NativeVRInput.GetVector2("ThumbstickLeft");

            if (stick.magnitude < 0.15f)
                stick = Vector2.zero;

            _cursorPosition += stick * VRConfig.MenuCursorSpeed * Time.unscaledDeltaTime;

            var rect = _canvas.GetComponent<RectTransform>();
            Vector2 half = rect.sizeDelta * 0.5f;
            _cursorPosition.x = Mathf.Clamp(_cursorPosition.x, -half.x, half.x);
            _cursorPosition.y = Mathf.Clamp(_cursorPosition.y, -half.y, half.y);

            if (_cursorRect != null)
                _cursorRect.anchoredPosition = _cursorPosition;
        }

        /// <summary>
        /// Works the control under the pointer directly, rather than through the event system.
        /// <para>
        /// The panel knows where its own controls are, so hit testing them here avoids depending on
        /// a mouse position round trip and on whichever camera the game leaves as the event camera.
        /// A slider stays held once grabbed, so it can be dragged past its own edges.
        /// </para>
        /// </summary>
        private void DriveControls()
        {
            // With a beam in use, only the pointing hand clicks: the other trigger takes the beam.
            bool click = VRConfig.LaserPointer
                ? VRLaser.ClickHeld()
                : NativeVRInput.GetBoolean("TriggerRight_Press") ||
                  NativeVRInput.GetBoolean("TriggerLeft_Press");

            bool pressed = click && !_lastClick;
            bool released = !click && _lastClick;
            _lastClick = click;

            if (released)
                _heldSlider = null;

            if (_heldSlider != null)
            {
                DragSlider(_heldSlider);
                return;
            }

            if (!pressed)
                return;

            foreach (var button in _buttons)
            {
                if (Hits(button.Rect, 8f))
                {
                    button.Action();
                    return;
                }
            }

            foreach (var row in _rows)
            {
                if (row.Toggle != null && Hits(row.Toggle.GetComponent<RectTransform>(), 20f))
                {
                    row.Toggle.isOn = !row.Toggle.isOn;
                    row.Setting.Write(row.Toggle.isOn ? 1f : 0f);
                    MelonLogger.Msg($"[VRSettingsPanel] {row.Setting.Label} = {row.Toggle.isOn}");
                    return;
                }

                if (row.Slider != null && Hits(row.Slider.GetComponent<RectTransform>(), 16f))
                {
                    _heldSlider = row.Slider;
                    DragSlider(row.Slider);
                    return;
                }
            }
        }

        private void DragSlider(Slider slider)
        {
            var rect = slider.GetComponent<RectTransform>();
            float left = rect.anchoredPosition.x;
            float fraction = Mathf.Clamp01((_cursorPosition.x + Widthed() - left) / rect.sizeDelta.x);

            slider.value = Mathf.Lerp(slider.minValue, slider.maxValue, fraction);

            foreach (var row in _rows)
            {
                if (row.Slider == slider)
                {
                    row.Setting.Write(slider.value);
                    break;
                }
            }
        }

        /// <summary>Cursor coordinates are centred, control positions are from the corner.</summary>
        private float Widthed()
        {
            return _canvas.GetComponent<RectTransform>().sizeDelta.x * 0.5f;
        }

        private bool Hits(RectTransform rect, float padding)
        {
            float x = _cursorPosition.x + Widthed();
            float y = _cursorPosition.y + _canvas.GetComponent<RectTransform>().sizeDelta.y * 0.5f;

            return x >= rect.anchoredPosition.x - padding &&
                   x <= rect.anchoredPosition.x + rect.sizeDelta.x + padding &&
                   y >= rect.anchoredPosition.y - padding &&
                   y <= rect.anchoredPosition.y + rect.sizeDelta.y + padding;
        }

        private void RefreshValues()
        {
            foreach (var row in _rows)
            {
                if (row.Slider != null)
                {
                    float current = row.Setting.Read();
                    if (!Mathf.Approximately(row.Slider.value, current))
                        row.Slider.value = Mathf.Clamp(current, row.Setting.Min, row.Setting.Max);

                    if (row.Value != null)
                        row.Value.text = current.ToString("F2");
                }
                else if (row.Toggle != null)
                {
                    bool on = row.Setting.Read() >= 0.5f;
                    if (row.Toggle.isOn != on)
                        row.Toggle.isOn = on;
                }
            }
        }

        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        private static Text AddLabel(Transform parent, string text, float x, float y, int size,
            TextAnchor anchor, float width = 0f)
        {
            var go = NewChild(parent, "Label");
            var label = go.AddComponent<Text>();

            label.text = text;
            label.fontSize = size;
            label.alignment = anchor;
            label.color = Color.white;
            label.raycastTarget = false;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                         ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            Anchor(go.GetComponent<RectTransform>(), x, y, width > 0f ? width : Width - x - Margin, RowHeight - 8f);
            return label;
        }

        private static void Anchor(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
