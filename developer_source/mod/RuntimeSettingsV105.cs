using MelonLoader;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.HighDefinition; // V137 measured eye input resolution
using Valve.VR;
using Mathf = UnityEngine.Mathf;

namespace SonsVRMod
{
    /// <summary>
    /// V105c: lightweight Luke-Ross-style in-HMD runtime settings + performance HUD.
    ///
    /// IMPORTANT V105c SAFETY RULE: VR render-scale changes are persisted for the NEXT LAUNCH
    /// instead of hot-resizing live HDRP eye RenderTextures. V105b proved that changing target
    /// dimensions under active persistent HDRP/AER/NVOFA state can hard-hang the D3D11 render
    /// thread without a managed exception. Other settings remain live-applicable.
    ///
    /// Xbox View+Menu held together toggles the panel. D-pad navigates/changes values,
    /// A applies+saves, B closes without applying, X restores defaults in the pending UI.
    ///
    /// Settings are persisted next to the mod DLL as SonsVR_Settings.ini. The file is a tiny
    /// key=value format deliberately chosen over reflection-heavy serializers so it remains
    /// robust under IL2CPP/MelonLoader updates and is human-editable outside VR.
    /// </summary>
    internal static class RuntimeSettingsV105
    {
        private sealed class Settings
        {
            public bool OpticalFlowEnabled = true;
            public float OpticalFlowResolutionScale = 0.50f;
            public float RenderResolutionScale = 1.00f;
            public float GameplayFarClipMeters = 24000f;
            public float MenuFarClipMeters = 50f;
            public float DepthGuardMaxToleranceMeters = 1.50f;
            // V112: user-selectable gameplay renderer. AER = one real eye + synthesized partner;
            // 2E = both eye cameras rendered genuinely every application frame.
            public bool GameplayAerEnabled = true;
            // V141 production AER: false = V140 winning Profile B; true = V103 rollback.
            public bool AerSpatialHoleFillEnabled = false;
            // Legacy V112 INI key retained only for backwards-compatible parsing. V113 no longer
            // moves HUD children independently; the whole native 2D sheet is compositor-positioned.
            public bool CenterHudVertically = false;
            public bool StatsOverlayEnabled = false;
            public bool PerfLogEnabled = true;
            public float PerfWarningFrameTimeMs = 50f;
            // V163 custom overlay sizes are apparent viewport fractions, independent of RT size.
            public float SettingsOverlayScale = 0.75f;
            public float StatsOverlayScale = 1.00f;
            // Fixed Ctrl+Alt modifiers minimize conflict with the game's own controls.
            public string KeyboardShortcutKey = "F10";
            // V168G: opt-in to original SteamVR -> synthetic keyboard/mouse gesture mapping.
            // Intentionally takes effect only at startup, never live during a held button.
            public bool LegacyVrGesturesEnabled = false;

            public Settings Clone()
            {
                return (Settings)MemberwiseClone();
            }
        }

        private static Settings _current = new Settings();
        private static Settings _pending = new Settings();
        private static bool _loaded;
        private static bool _initialRuntimeApplied;
        private static string _settingsPath;

        private static GameObject _settingsGo;
        // V111: V110 two-column UI retained; panel is now 2x apparent size.
        private static Text _settingsHeaderTextV110;
        private static Text _settingsHintTextV110;
        private static Text _settingsFooterTextV110;
        private const int RowCount = 18;
        private const int ApplyRow = 16;
        private const int CloseRow = 17;
        private static readonly Text[] _settingsLabelTextV110 = new Text[RowCount];
        private static readonly Text[] _settingsValueTextV110 = new Text[RowCount];
        private static readonly Image[] _settingsRowBgV110 = new Image[RowCount];

        // V111: preserve the native pause itself, but temporarily suspend its UGUI interaction
        // while our raw-Gamepad settings overlay owns D-pad/A/B/X. Exact enabled/interactable
        // states are restored after the overlay closes AND the buttons used to close it are released.
        private static Selectable[] _nativePauseSelectablesV110;
        private static bool[] _nativePauseSelectableStatesV110;
        private static GraphicRaycaster[] _nativePauseRaycastersV110;
        private static bool[] _nativePauseRaycasterStatesV110;
        private static BaseInputModule _nativePauseInputModuleV110;
        private static bool _nativePauseInputModuleWasEnabledV110;
        private static EventSystem _nativePauseEventSystemV110;
        private static bool _nativePauseSendNavigationWasEnabledV110;
        private static GameObject _nativePauseSelectedGoV110;
        private static bool _nativePauseInputSuppressedV110;
        private static bool _nativePauseRestorePendingV110;

        private static GameObject _statsGo;
        private static Text _statsText;
        // V137: sampled HDRP input, not targetTexture allocation or guessed DLSS percentage.
        private static float _nextStatsResolutionPoll;
        private static string _statsResolutionText = "DLSS: -- X --";
        private static bool _panelOpen;
        private static int _selected;
        private static bool _dirty;
        private static float _comboHeldSince = -1f;
        private static bool _comboLatched;
        private static float _messageUntil;
        private static string _message = "";

        private static float _emaDtMs = 40f;
        private static float _nextStatsUpdate;
        private static float _clientFrameMs;
        private static float _gpuMs;
        private static uint _droppedFrames;
        private static uint _reprojectionFlags;

        private static StreamWriter _perfWriter;
        private static string _perfPath;
        private static float _nextPerfSample;
        private static float _nextPerfFlush;
        private static float _nextPerfWarningAllowed;

        private static readonly float[] OfScales =
        {
            0.25f, 0.375f, 0.50f, 0.75f, 1.00f
        };

        private static readonly float[] RenderScales =
        {
            0.60f, 0.70f, 0.80f, 0.90f, 1.00f, 1.10f, 1.20f
        };

        private static readonly float[] GameplayClips =
        {
            150f, 250f, 500f, 1000f, 2500f, 5000f, 12000f, 24000f
        };

        private static readonly float[] MenuClips =
        {
            25f, 35f, 50f, 75f, 100f, 150f, 250f
        };

        private static readonly float[] GuardCaps =
        {
            0.50f, 0.75f, 1.00f, 1.25f, 1.50f, 2.00f, 3.00f, 6.00f
        };

        private static readonly float[] OverlayScales =
        {
            0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.75f, 0.80f,
            0.90f, 1.00f, 1.10f, 1.25f, 1.50f, 1.75f, 2.00f, 2.50f, 3.00f
        };

        private static readonly string[] ShortcutKeys =
        {
            "F8", "F9", "F10", "F11", "F12", "Insert", "Home", "End"
        };

        private static readonly float[] WarningThresholds =
        {
            25f, 33f, 40f, 50f, 66f, 80f, 100f
        };

        internal static void LoadEarly()
        {
            if (_loaded) return;
            _loaded = true;
            _settingsPath = ResolveSettingsPath();
            _current = LoadFromDisk(_settingsPath) ?? new Settings();
            Sanitize(_current);
            _pending = _current.Clone();
            ApplyStaticFields(_current);
            // V168G: load mode before InitVR() and before the first input frame.
            // Don't change the mode in ApplyRuntime: switching while E/mouse is held
            // could strand synthetic input state and corrupt native menu ownership.
            SonsVRMod.GamepadOnlyMode = !_current.LegacyVrGesturesEnabled;
            MelonLogger.Warning("[V168G Gesture] Startup input mode=" +
                (_current.LegacyVrGesturesEnabled ? "LEGACY_STEAMVR_GESTURES" : "NATIVE_GAMEPAD") +
                "; changes require full game restart; GamepadCursorMode unchanged.");
            // V168G: migrate outdated INI schemas once, keeping old user values for known keys.
            // Save a single pre-migration backup; new fields appear even if the mod is only updated.
            try
            {
                string[] existingLines = File.Exists(_settingsPath)
                    ? File.ReadAllLines(_settingsPath) : new string[0];
                bool needsUpgrade = !existingLines.Any(line =>
                        line.Trim().Equals("SettingsSchemaVersion=168", StringComparison.OrdinalIgnoreCase)) ||
                    !existingLines.Any(line => line.Trim().StartsWith("SettingsOverlayScale=", StringComparison.OrdinalIgnoreCase)) ||
                    !existingLines.Any(line => line.Trim().StartsWith("StatsOverlayScale=", StringComparison.OrdinalIgnoreCase)) ||
                    !existingLines.Any(line => line.Trim().StartsWith("KeyboardShortcutKey=", StringComparison.OrdinalIgnoreCase)) ||
                    !existingLines.Any(line => line.Trim().StartsWith("LegacyVrGesturesEnabled=", StringComparison.OrdinalIgnoreCase)) ||
                    existingLines.Any(line => line.Trim().StartsWith("DlssEnabled=", StringComparison.OrdinalIgnoreCase) ||
                        line.Trim().StartsWith("DlssQuality=", StringComparison.OrdinalIgnoreCase));
                if (needsUpgrade)
                {
                    if (File.Exists(_settingsPath))
                    {
                        string backup = _settingsPath + ".preV168G.bak";
                        if (!File.Exists(backup)) File.Copy(_settingsPath, backup);
                    }
                    SaveToDisk(_settingsPath, _current);
                    MelonLogger.Msg("[V168G Settings] INI schema upgraded to 168; old settings backup preserved.");
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V163 Settings] INI migration deferred: " + e.Message); }
            MelonLogger.Msg("[V141 AER-PROFILE] Settings source=" + _settingsPath +
                " spatialFill=" + (_current.AerSpatialHoleFillEnabled ? "V103_GUARDED_ROLLBACK" : "OFF_PROFILE_B") +
                " V100RotationFallback=ON");
            MelonLogger.Msg($"[V116 Settings] Loaded '{_settingsPath}'. mode={(_current.GameplayAerEnabled ? "AER" : "2E")} nativeHUD=WORLD_PLANE@{UnifiedHudWorldPlaneV116.DistanceMeters:F2}m/{UnifiedHudWorldPlaneV116.HorizontalFovDegrees:F0}deg DLSS=native-game-menu OF={_current.OpticalFlowEnabled}@{_current.OpticalFlowResolutionScale:F3} renderScale={_current.RenderResolutionScale:F2} clips={_current.MenuFarClipMeters:F0}/{_current.GameplayFarClipMeters:F0}m guardCap={_current.DepthGuardMaxToleranceMeters:F2}m.");
        }

        internal static void Tick()
        {
            if (!_loaded) LoadEarly();

            float dtMs = Mathf.Max(0.01f, Time.unscaledDeltaTime * 1000f);
            _emaDtMs = Mathf.Lerp(_emaDtMs, dtMs, 0.08f);

            VRPlayer player = VRPlayer.Instance;
            StereoRender stereo = player != null ? player.StereoRender : null;

            if (!_initialRuntimeApplied && stereo != null && stereo.LeftCam != null && stereo.RightCam != null)
            {
                _initialRuntimeApplied = true;
                ApplyRuntime(_current, forceRebuild: false, persist: false);
            }

            UpdateNativePauseRestoreV110();
            UpdatePanelToggle();
            if (_panelOpen)
            {
                EnsureNativePauseMenuSuppressedV110();
                EnsureSettingsPanel(stereo);
                PlaceWorldCanvasAtStereoViewportV112(_settingsGo, stereo,
                    0.50f, 0.52f, 0.66f * _pending.SettingsOverlayScale, 1.25f);
                UpdatePanelInput();
                RefreshSettingsPanelText();
            }
            else if (_settingsGo != null && _settingsGo.activeSelf)
            {
                _settingsGo.SetActive(false);
            }

            UpdateCompositorTiming();
            UpdateStatsOverlay(stereo);
            UpdatePerfLogger(stereo, player, dtMs);
            PerformanceForensicsV106.Tick(stereo, player);
        }

        internal static void Shutdown()
        {
            try
            {
                _perfWriter?.Flush();
                _perfWriter?.Dispose();
            }
            catch { }
            _perfWriter = null;
            RestoreNativePauseMenuInputV110();
        }

        private static void UpdatePanelToggle()
        {
            // V163: View+Menu is the *only* controller chord. L3+R3 was removed.
            Gamepad g = Gamepad.current;
            bool chord = g != null && g.selectButton.isPressed && g.startButton.isPressed;
            if (chord)
            {
                if (_comboHeldSince < 0f) _comboHeldSince = Time.unscaledTime;
                if (!_comboLatched && Time.unscaledTime - _comboHeldSince >= 0.45f)
                {
                    _comboLatched = true;
                    SetPanelOpen(!_panelOpen);
                }
            }
            else
            {
                _comboHeldSince = -1f;
                _comboLatched = false;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (!KeyboardModifiersPressedV163(keyboard)) return;
            Key shortcut;
            if (!Enum.TryParse<Key>(_current.KeyboardShortcutKey, true, out shortcut)) return;
            if (keyboard[shortcut].wasPressedThisFrame) SetPanelOpen(!_panelOpen);
        }

        private static bool KeyboardModifiersPressedV163(Keyboard k)
        {
            return (k.leftCtrlKey.isPressed || k.rightCtrlKey.isPressed) &&
                   (k.leftAltKey.isPressed || k.rightAltKey.isPressed);
        }

        private static void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (open)
            {
                _nativePauseRestorePendingV110 = false;
                _pending = _current.Clone();
                _dirty = false;
                _message = "";
                _selected = Mathf.Clamp(_selected, 0, RowCount - 1);
                // The View+Menu chord is intentionally NOT swallowed: Menu still opens the game's
                // pause screen first, so Time.timeScale/menu semantics remain native. We only make
                // that already-open menu non-interactive while this overlay has control.
                EnsureNativePauseMenuSuppressedV110();
            }
            else if (_nativePauseInputSuppressedV110)
            {
                // Do not restore on the same B/View/Menu press that closes our overlay or the
                // native menu can consume that very same edge and immediately back out/unpause.
                _nativePauseRestorePendingV110 = true;
            }
            if (_settingsGo != null)
                _settingsGo.SetActive(open);
        }

        // V111: renderer may temporarily render both eyes while the settings panel is open.
        // This is deliberately read-only; input ownership remains here.
        internal static bool IsPanelOpenV111 => _panelOpen;

        private static bool OverlayControlButtonsReleasedV110()
        {
            Gamepad g = Gamepad.current;
            if (g == null) return true;
            return !g.selectButton.isPressed && !g.startButton.isPressed &&
                   !g.leftStickButton.isPressed && !g.rightStickButton.isPressed &&
                   !g.buttonSouth.isPressed && !g.buttonEast.isPressed &&
                   !g.buttonWest.isPressed && !g.buttonNorth.isPressed &&
                   !g.dpad.up.isPressed && !g.dpad.down.isPressed &&
                   !g.dpad.left.isPressed && !g.dpad.right.isPressed;
        }

        private static void UpdateNativePauseRestoreV110()
        {
            if (!_nativePauseRestorePendingV110) return;
            if (!OverlayControlButtonsReleasedV110()) return;
            _nativePauseRestorePendingV110 = false;
            RestoreNativePauseMenuInputV110();
        }

        private static GameObject FindNativePauseMenuV110()
        {
            try
            {
                GameObject exact = GameObject.Find("PlayerStandin/PlayerUI/ScreenSpaceCanvas/PauseMenu");
                if (exact != null) return exact;
                foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
                    if (t != null && t.name == "PauseMenu" && t.gameObject.activeInHierarchy)
                        return t.gameObject;
            }
            catch { }
            return null;
        }

        private static void EnsureNativePauseMenuSuppressedV110()
        {
            if (_nativePauseInputSuppressedV110) return;

            // The exact pause hierarchy can vary between menu states/builds. EventSystem suppression
            // is therefore the authoritative guard; pause-root Selectables/Raycasters are an extra
            // belt-and-suspenders layer when the root is discoverable. The overlay itself reads
            // Gamepad.current directly, so it stays fully interactive while Unity UI navigation is off.
            GameObject pause = FindNativePauseMenuV110();

            try
            {
                _nativePauseSelectablesV110 = pause != null ? pause.GetComponentsInChildren<Selectable>(true) : new Selectable[0];
                _nativePauseSelectableStatesV110 = new bool[_nativePauseSelectablesV110.Length];
                for (int i = 0; i < _nativePauseSelectablesV110.Length; ++i)
                {
                    Selectable x = _nativePauseSelectablesV110[i];
                    if (x == null) continue;
                    _nativePauseSelectableStatesV110[i] = x.interactable;
                    x.interactable = false;
                }

                _nativePauseRaycastersV110 = pause != null ? pause.GetComponentsInChildren<GraphicRaycaster>(true) : new GraphicRaycaster[0];
                _nativePauseRaycasterStatesV110 = new bool[_nativePauseRaycastersV110.Length];
                for (int i = 0; i < _nativePauseRaycastersV110.Length; ++i)
                {
                    GraphicRaycaster x = _nativePauseRaycastersV110[i];
                    if (x == null) continue;
                    _nativePauseRaycasterStatesV110[i] = x.enabled;
                    x.enabled = false;
                }

                _nativePauseEventSystemV110 = EventSystem.current;
                if (_nativePauseEventSystemV110 != null)
                {
                    _nativePauseSelectedGoV110 = _nativePauseEventSystemV110.currentSelectedGameObject;
                    _nativePauseSendNavigationWasEnabledV110 = _nativePauseEventSystemV110.sendNavigationEvents;
                    _nativePauseEventSystemV110.sendNavigationEvents = false;

                    _nativePauseInputModuleV110 = _nativePauseEventSystemV110.currentInputModule;
                    if (_nativePauseInputModuleV110 != null)
                    {
                        _nativePauseInputModuleWasEnabledV110 = _nativePauseInputModuleV110.enabled;
                        _nativePauseInputModuleV110.enabled = false;
                    }
                    _nativePauseEventSystemV110.SetSelectedGameObject(null);
                }

                _nativePauseInputSuppressedV110 = true;
                MelonLogger.Msg($"[V111 Settings] Native pause interaction suspended while overlay is open: pauseRoot={(pause != null ? pause.name : "not-found")}, selectables={_nativePauseSelectablesV110.Length} raycasters={_nativePauseRaycastersV110.Length} inputModule={(_nativePauseInputModuleV110 != null ? _nativePauseInputModuleV110.GetType().Name : "none")}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V111 Settings] Pause-menu input suppression failed: " + e.Message);
            }
        }

        private static void RestoreNativePauseMenuInputV110()
        {
            if (!_nativePauseInputSuppressedV110) return;
            try
            {
                if (_nativePauseSelectablesV110 != null && _nativePauseSelectableStatesV110 != null)
                {
                    int n = Mathf.Min(_nativePauseSelectablesV110.Length, _nativePauseSelectableStatesV110.Length);
                    for (int i = 0; i < n; ++i)
                        if (_nativePauseSelectablesV110[i] != null)
                            _nativePauseSelectablesV110[i].interactable = _nativePauseSelectableStatesV110[i];
                }
                if (_nativePauseRaycastersV110 != null && _nativePauseRaycasterStatesV110 != null)
                {
                    int n = Mathf.Min(_nativePauseRaycastersV110.Length, _nativePauseRaycasterStatesV110.Length);
                    for (int i = 0; i < n; ++i)
                        if (_nativePauseRaycastersV110[i] != null)
                            _nativePauseRaycastersV110[i].enabled = _nativePauseRaycasterStatesV110[i];
                }
                if (_nativePauseInputModuleV110 != null)
                    _nativePauseInputModuleV110.enabled = _nativePauseInputModuleWasEnabledV110;
                if (_nativePauseEventSystemV110 != null)
                {
                    _nativePauseEventSystemV110.sendNavigationEvents = _nativePauseSendNavigationWasEnabledV110;
                    if (_nativePauseSelectedGoV110 != null && _nativePauseSelectedGoV110.activeInHierarchy)
                        _nativePauseEventSystemV110.SetSelectedGameObject(_nativePauseSelectedGoV110);
                }
            }
            catch { }
            _nativePauseSelectablesV110 = null;
            _nativePauseSelectableStatesV110 = null;
            _nativePauseRaycastersV110 = null;
            _nativePauseRaycasterStatesV110 = null;
            _nativePauseInputModuleV110 = null;
            _nativePauseEventSystemV110 = null;
            _nativePauseSelectedGoV110 = null;
            _nativePauseInputSuppressedV110 = false;
            MelonLogger.Msg("[V111 Settings] Native pause interaction restored.");
        }

        private static void UpdatePanelInput()
        {
            Gamepad g = Gamepad.current;
            Keyboard k = Keyboard.current;
            bool up = (g != null && g.dpad.up.wasPressedThisFrame) ||
                      (k != null && k.upArrowKey.wasPressedThisFrame);
            bool down = (g != null && g.dpad.down.wasPressedThisFrame) ||
                        (k != null && k.downArrowKey.wasPressedThisFrame);
            bool left = (g != null && g.dpad.left.wasPressedThisFrame) ||
                        (k != null && k.leftArrowKey.wasPressedThisFrame);
            bool right = (g != null && g.dpad.right.wasPressedThisFrame) ||
                         (k != null && k.rightArrowKey.wasPressedThisFrame);
            if (up) _selected = (_selected + RowCount - 1) % RowCount;
            if (down) _selected = (_selected + 1) % RowCount;
            if (left) AdjustSelected(-1);
            if (right) AdjustSelected(+1);

            bool accept = (g != null && g.buttonSouth.wasPressedThisFrame) ||
                          (k != null && k.enterKey.wasPressedThisFrame);
            if (accept)
            {
                if (_selected == ApplyRow)
                {
                    Sanitize(_pending);
                    float liveScaleBefore = StereoRender.RenderResolutionScale;
                    bool scaleDeferred = Mathf.Abs(liveScaleBefore - _pending.RenderResolutionScale) > 0.001f;
                    bool gestureDeferred = _pending.LegacyVrGesturesEnabled == SonsVRMod.GamepadOnlyMode;
                    ApplyRuntime(_pending, forceRebuild: false, persist: true);
                    _current = _pending.Clone();
                    _dirty = false;
                    _message = (scaleDeferred || gestureDeferred)
                        ? "APPLIED + SAVED — restart game to activate " +
                          (gestureDeferred ? "gesture-control change" : "render-scale change") +
                          (scaleDeferred && gestureDeferred ? " and render-scale change" : "")
                        : "APPLIED + SAVED";
                    _messageUntil = Time.unscaledTime + ((scaleDeferred || gestureDeferred) ? 6.0f : 2.0f);
                }
                else if (_selected == CloseRow)
                    SetPanelOpen(false);
                else if (_selected == 0)
                    RecenterViewV163();
                else AdjustSelected(+1);
            }
            if ((g != null && g.buttonEast.wasPressedThisFrame) ||
                (k != null && k.escapeKey.wasPressedThisFrame)) SetPanelOpen(false);

            if (g != null && g.buttonWest.wasPressedThisFrame)
            {
                _pending = new Settings();
                _dirty = true;
                _message = "DEFAULTS LOADED (A on APPLY to commit)";
                _messageUntil = Time.unscaledTime + 2.0f;
            }
        }

        private static void RecenterViewV163()
        {
            VRPlayer player = VRPlayer.Instance;
            if (player == null || player.Origin == null || player.StereoRender == null ||
                player.StereoRender.Head == null || player.LocalPlayer == null ||
                player.InventoryCamIsActive || SceneManager.GetActiveScene().name != "SonsMain")
            {
                _message = "RECENTER: available in normal gameplay only";
                _messageUntil = Time.unscaledTime + 2.5f;
                return;
            }
            // Explicit user action only. Retarget the tracked head to the avatar's X/Z
            // position and yaw without changing physical head height or headset pitch/roll.
            // This is a Unity-rig recenter, NOT an OpenVR tracking-universe reset.
            Transform origin = player.Origin;
            Transform head = player.StereoRender.Head;
            Vector3 before = head.position;
            Vector3 intended = player.LocalPlayer.transform.forward;
            Vector3 current = head.forward;
            intended.y = 0f; current.y = 0f;
            float correction = 0f;
            if (intended.sqrMagnitude > 0.01f && current.sqrMagnitude > 0.01f)
            {
                correction = Vector3.SignedAngle(current, intended, Vector3.up);
                origin.rotation = Quaternion.AngleAxis(correction, Vector3.up) * origin.rotation;
            }
            Vector3 target = player.LocalPlayer.transform.position;
            Vector3 headAfter = head.position;
            Vector3 delta = new Vector3(target.x - headAfter.x, 0f, target.z - headAfter.z);
            // Guard huge displacements during unusual gameplay/controller transitions.
            bool appliedHorizontal = delta.sqrMagnitude <= 4f;
            if (appliedHorizontal) origin.position += delta;
            MelonLogger.Msg($"[V163 Recenter] yaw={correction:F1} xzDelta={delta} appliedXZ={appliedHorizontal} headBefore={before} headAfter={head.position}");
            _message = appliedHorizontal
                ? "VIEW RECENTERED (YAW + HORIZONTAL, HEIGHT UNCHANGED)"
                : "YAW RECENTERED; HORIZONTAL OFFSET TOO LARGE (SKIPPED)";
            _messageUntil = Time.unscaledTime + 3f;
        }

        private static void AdjustSelected(int direction)
        {
            switch (_selected)
            {
                case 0: // Recenter is an action (A/Enter), not a selectable value.
                    return;
                case 1: _pending.OpticalFlowEnabled = !_pending.OpticalFlowEnabled; break;
                case 2: _pending.OpticalFlowResolutionScale = StepFloat(OfScales, _pending.OpticalFlowResolutionScale, direction); break;
                case 3: _pending.RenderResolutionScale = StepFloat(RenderScales, _pending.RenderResolutionScale, direction); break;
                case 4: _pending.GameplayFarClipMeters = StepFloat(GameplayClips, _pending.GameplayFarClipMeters, direction); break;
                case 5: _pending.MenuFarClipMeters = StepFloat(MenuClips, _pending.MenuFarClipMeters, direction); break;
                case 6: _pending.DepthGuardMaxToleranceMeters = StepFloat(GuardCaps, _pending.DepthGuardMaxToleranceMeters, direction); break;
                case 7: _pending.GameplayAerEnabled = !_pending.GameplayAerEnabled; break;
                case 8: return; // informational HUD row, unchanged
                case 9: _pending.StatsOverlayEnabled = !_pending.StatsOverlayEnabled; break;
                case 10: _pending.PerfLogEnabled = !_pending.PerfLogEnabled; break;
                case 11: _pending.PerfWarningFrameTimeMs = StepFloat(WarningThresholds, _pending.PerfWarningFrameTimeMs, direction); break;
                case 12: _pending.SettingsOverlayScale = StepFloat(OverlayScales, _pending.SettingsOverlayScale, direction); break;
                case 13: _pending.StatsOverlayScale = StepFloat(OverlayScales, _pending.StatsOverlayScale, direction); break;
                case 14: _pending.KeyboardShortcutKey = StepString(ShortcutKeys, _pending.KeyboardShortcutKey, direction); break;
                case 15: _pending.LegacyVrGesturesEnabled = !_pending.LegacyVrGesturesEnabled; break;
                case ApplyRow:
                case CloseRow:
                    return;
            }
            _dirty = true;
        }

        private static string StepString(string[] values, string current, int direction)
        {
            int best = 0;
            for (int i = 0; i < values.Length; ++i)
                if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase)) best = i;
            best = (best + (direction < 0 ? -1 : 1) + values.Length) % values.Length;
            return values[best];
        }

        private static float StepFloat(float[] values, float current, int direction)
        {
            int best = 0;
            float bestErr = float.MaxValue;
            for (int i = 0; i < values.Length; ++i)
            {
                float e = Mathf.Abs(values[i] - current);
                if (e < bestErr) { bestErr = e; best = i; }
            }
            best = Mathf.Clamp(best + (direction < 0 ? -1 : 1), 0, values.Length - 1);
            return values[best];
        }

        private static Text CreatePanelTextV110(Transform parent, string name, int fontSize, TextAnchor anchor, Color color,
            Vector2 size, Vector2 anchoredPosition)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = fontSize;
            t.alignment = anchor;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPosition;
            return t;
        }

        private static Image CreatePanelImageV110(Transform parent, string name, Color color,
            Vector2 size, Vector2 anchoredPosition)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Image img = go.AddComponent<Image>();
            img.color = color;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchoredPosition;
            return img;
        }

        private static void EnsureSettingsPanel(StereoRender stereo)
        {
            if (_settingsGo != null)
            {
                if (!_settingsGo.activeSelf) _settingsGo.SetActive(true);
                return;
            }
            if (stereo == null || stereo.Head == null) return;

            _settingsGo = new GameObject("SonsVR_V112_SettingsOverlay");
            Canvas canvas = _settingsGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 6000;
            _settingsGo.layer = LayerMask.NameToLayer("Default");

            RectTransform rt = _settingsGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1320f, 1140f);
            _settingsGo.transform.localScale = Vector3.one * 0.00068f; // V112 fallback only; normalized stereo viewport placement normally owns scale.

            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(_settingsGo.transform, false);
            Image bg = bgGo.AddComponent<Image>();
            bg.color = new Color(0.015f, 0.02f, 0.025f, 0.96f);
            RectTransform bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;

            Color border = new Color(0.72f, 0.75f, 0.80f, 0.90f);
            Color divider = new Color(0.42f, 0.46f, 0.52f, 0.85f);
            Color rowNormal = new Color(0f, 0f, 0f, 0.16f);
            float tableTop = -205f;
            float rowH = 43f; // V168G: fit one extra opt-in gesture row without clipping footer
            float tableW = 1210f;

            _settingsHeaderTextV110 = CreatePanelTextV110(_settingsGo.transform, "Header", 46, TextAnchor.MiddleCenter,
                Color.white, new Vector2(1210f, 58f), new Vector2(0f, -24f));
            _settingsHeaderTextV110.text = "SONS VR  /  RUNTIME SETTINGS  V168G";

            _settingsHintTextV110 = CreatePanelTextV110(_settingsGo.transform, "Hint", 30, TextAnchor.MiddleCenter,
                new Color(0.86f, 0.89f, 0.94f, 1f), new Vector2(1210f, 106f), new Vector2(0f, -84f));
            _settingsHintTextV110.text =
                "View+Menu or Ctrl+Alt+F10: toggle   |   D-pad / arrows: navigate\n" +
                "A / Enter: activate   B / Esc: close   X: defaults\n" +
                "Use main menu OPTIONS - GRAPHICS for DLSS";

            // Real table geometry: outer border, explicit label/value divider, row separators.
            float tableHeight = RowCount * rowH;
            CreatePanelImageV110(_settingsGo.transform, "TableTop", border, new Vector2(tableW, 3f), new Vector2(0f, tableTop));
            CreatePanelImageV110(_settingsGo.transform, "TableBottom", border, new Vector2(tableW, 3f), new Vector2(0f, tableTop - tableHeight));
            CreatePanelImageV110(_settingsGo.transform, "TableLeft", border, new Vector2(3f, tableHeight), new Vector2(-tableW * 0.5f, tableTop));
            CreatePanelImageV110(_settingsGo.transform, "TableRight", border, new Vector2(3f, tableHeight), new Vector2(tableW * 0.5f, tableTop));
            CreatePanelImageV110(_settingsGo.transform, "TableDivider", divider, new Vector2(3f, tableHeight), new Vector2(-115f, tableTop));

            for (int i = 0; i < RowCount; ++i)
            {
                float y = tableTop - i * rowH - rowH * 0.5f;
                _settingsRowBgV110[i] = CreatePanelImageV110(_settingsGo.transform, "RowBG_" + i,
                    rowNormal, new Vector2(tableW - 6f, rowH - 2f), new Vector2(0f, y + rowH * 0.5f));
                if (i > 0)
                    CreatePanelImageV110(_settingsGo.transform, "RowLine_" + i, divider,
                        new Vector2(tableW, 2f), new Vector2(0f, tableTop - i * rowH));

                _settingsLabelTextV110[i] = CreatePanelTextV110(_settingsGo.transform, "Label_" + i, 31,
                    TextAnchor.MiddleLeft, Color.white, new Vector2(455f, rowH - 4f), new Vector2(-350f, y + rowH * 0.5f));
                _settingsValueTextV110[i] = CreatePanelTextV110(_settingsGo.transform, "Value_" + i, 31,
                    TextAnchor.MiddleLeft, new Color(0.93f, 0.95f, 1f, 1f), new Vector2(675f, rowH - 4f), new Vector2(245f, y + rowH * 0.5f));
            }

            _settingsFooterTextV110 = CreatePanelTextV110(_settingsGo.transform, "Footer", 27, TextAnchor.UpperLeft,
                new Color(0.90f, 0.92f, 0.96f, 1f), new Vector2(1210f, 105f), new Vector2(0f, tableTop - tableHeight - 18f));

            _settingsGo.transform.SetParent(stereo.Head, false);
            _settingsGo.transform.localPosition = new Vector3(0f, 0.02f, 0.72f);
            _settingsGo.transform.localRotation = Quaternion.identity;
            _settingsGo.SetActive(true);
            MelonLogger.Msg("[V112 Settings] Two-column settings overlay created; placement is normalized stereo viewport space, not guessed head-local meters. Native pause UI is suspended while this overlay owns input.");
        }

        private static void RefreshSettingsPanelText()
        {
            if (_settingsLabelTextV110[0] == null) return;
            if (_settingsHintTextV110 != null)
                _settingsHintTextV110.text =
                    "View+Menu or Ctrl+Alt+" + _current.KeyboardShortcutKey +
                    ": toggle    D-pad / arrows: navigate\n" +
                    "A / Enter: activate   B / Esc: close   X: defaults\n" +
                    "Use main menu OPTIONS - GRAPHICS for DLSS";

            string[] names = new string[RowCount]
            {
                "RECENTER VIEW", "Optical flow / NVOFA", "OF input resolution",
                "VR render scale", "Gameplay far clip", "Menu / cooking far clip",
                "Depth-guard max dZ", "Gameplay render mode", "HUD presentation",
                "Frame-time overlay", "Lightweight perf log", "Spike warning threshold",
                "Settings menu size", "FPS overlay size", "Keyboard toggle (Ctrl+Alt+)",
                "Legacy VR gestures", "APPLY + SAVE", "CLOSE"
            };

            // V168G text-only: show ON/OFF beside the label, leaving the entire value cell for the warning.
            names[15] = "Legacy VR gestures: " + (_pending.LegacyVrGesturesEnabled ? "ON" : "OFF");

            float ofArea = _pending.OpticalFlowResolutionScale * _pending.OpticalFlowResolutionScale * 100f;
            float liveRenderScale = StereoRender.RenderResolutionScale;
            string scaleValue = $"{_pending.RenderResolutionScale:F2}x  [restart-required]";
            if (Mathf.Abs(_pending.RenderResolutionScale - liveRenderScale) > 0.001f)
                scaleValue += $"  live={liveRenderScale:F2}x";

            string actualModeV112 = VRPlayer.Instance != null && VRPlayer.Instance.StereoRender != null
                ? VRPlayer.Instance.StereoRender.GetActualRenderModeV112()
                : "2E/PRE";
            string[] values = new string[RowCount]
            {
                "[A / Enter to recenter]",
                _pending.OpticalFlowEnabled ? "ON" : "OFF",
                $"{_pending.OpticalFlowResolutionScale * 100f:F1}% linear  ({ofArea:F1}% pixels)",
                scaleValue,
                $"{_pending.GameplayFarClipMeters:F0} m",
                $"{_pending.MenuFarClipMeters:F0} m",
                $"{_pending.DepthGuardMaxToleranceMeters:F2} m",
                (_pending.GameplayAerEnabled ? "AER" : "2E") + $"   [actual {actualModeV112}]",
                $"NATIVE 2D -> WORLD PLANE  {UnifiedHudWorldPlaneV116.DistanceMeters:F2} m / {UnifiedHudWorldPlaneV116.HorizontalFovDegrees:F0} deg",
                _pending.StatsOverlayEnabled ? "ON" : "OFF",
                _pending.PerfLogEnabled ? "ON (2 Hz + spikes)" : "OFF",
                $"{_pending.PerfWarningFrameTimeMs:F0} ms",
                $"{_pending.SettingsOverlayScale:F2}x   (0.20 - 3.00)",
                $"{_pending.StatsOverlayScale:F2}x   (0.20 - 3.00)",
                "Ctrl+Alt+" + _pending.KeyboardShortcutKey,
                "(CURRENTLY NOT SUPPORTED; leave OFF)",
                _dirty ? "[pending changes]" : "[no changes]",
                ""
            };

            Color selectedBg = new Color(0.20f, 0.28f, 0.38f, 0.82f);
            Color normalBg = new Color(0f, 0f, 0f, 0.16f);
            Color selectedText = new Color(1f, 0.93f, 0.35f, 1f);
            for (int i = 0; i < RowCount; ++i)
            {
                bool sel = i == _selected;
                _settingsLabelTextV110[i].text = (sel ? "▶  " : "    ") + names[i];
                _settingsValueTextV110[i].text = values[i];
                _settingsLabelTextV110[i].color = sel ? selectedText : Color.white;
                _settingsValueTextV110[i].color = sel ? selectedText : new Color(0.93f, 0.95f, 1f, 1f);
                if (_settingsRowBgV110[i] != null) _settingsRowBgV110[i].color = sel ? selectedBg : normalBg;
            }

            if (_settingsFooterTextV110 != null)
            {
                string perf = $"Live: ~{1000f / Mathf.Max(1f, _emaDtMs):F1} FPS / {_emaDtMs:F1} ms     SteamVR {_clientFrameMs:F1} ms     GPU {_gpuMs:F1} ms";
                string msg = (!string.IsNullOrEmpty(_message) && Time.unscaledTime < _messageUntil) ? ("\n" + _message) : "";
                _settingsFooterTextV110.text = perf + msg +
                    "\nRender scale / VR gesture mode: next launch only. DLSS: OPTIONS - GRAPHICS.";
            }
        }

        private static void ApplyRuntime(Settings next, bool forceRebuild, bool persist)
        {
            Sanitize(next);

            StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
            VRPlayer player = VRPlayer.Instance;

            float liveRenderScaleBefore = StereoRender.RenderResolutionScale;
            bool renderScaleChanged = Mathf.Abs(liveRenderScaleBefore - next.RenderResolutionScale) > 0.001f;
            bool runtimeEyesExist = stereo != null && stereo.LeftCam != null && stereo.RightCam != null;
            bool deferRenderScaleToRestart = renderScaleChanged && runtimeEyesExist;
            bool ofChanged = StereoRender.AerTemporalOpticalFlowV90Enabled != next.OpticalFlowEnabled ||
                             Mathf.Abs(SonsVRMod.V91NvofaResolutionScale - next.OpticalFlowResolutionScale) > 0.001f;

            // V105c crash breadcrumb: a user-triggered settings apply is rare, so synchronously
            // flush BEGIN/END event rows. If a native/GPU hang occurs, we retain the exact last
            // requested transition plus every previously buffered 2-Hz sample.
            if (persist)
            {
                WritePerfEventAndFlush(
                    "SETTINGS_APPLY_BEGIN " +
                    "DLSS=native-menu " +
                    $"OF={next.OpticalFlowEnabled}@{next.OpticalFlowResolutionScale:F3} " +
                    $"renderScaleRequested={next.RenderResolutionScale:F2} liveScale={liveRenderScaleBefore:F2} " +
                    $"clip={next.MenuFarClipMeters:F0}/{next.GameplayFarClipMeters:F0} " +
                    $"guard={next.DepthGuardMaxToleranceMeters:F2} mode={(next.GameplayAerEnabled ? "AER" : "2E")} nativeHUD=WORLD_PLANE",
                    stereo, player);
            }

            ApplyStaticFields(next);

            // V112: the desired gameplay mode is safe to change live. Never activate AER at the
            // title/loading cameras; only change the actual renderer after the proven gameplay gate.
            // StereoRender's existing enabled-state transition detector performs the history reset /
            // both-eye warmup when AER is enabled again.
            if (StereoRender.AerGameplayReadyForActivation)
                StereoRender.AlternateEyeRenderingEnabled = next.GameplayAerEnabled;

            // V105b hard-hang fix: changing LeftRT/RightRT dimensions live while persistent HDRP
            // history, AER submission and NVOFA render-thread work are active can freeze D3D11
            // after the resize with no managed exception. Static/startup scale changes are proven
            // safe, so persist the requested value but keep THIS session's live scale unchanged.
            if (deferRenderScaleToRestart)
                StereoRender.RenderResolutionScale = liveRenderScaleBefore;


            if (stereo != null)
            {
                // Do not rebuild eye targets for a render-scale edit. Scale is next-launch only.
                // DLSS is owned by the game's native graphics menu, not this panel.
                bool rebuild = forceRebuild;
                stereo.ApplyRuntimeSettingsV105(rebuild);

                if (ofChanged)
                    NvOpticalFlowV90.ResetForActivation();

                float currentFar = stereo.LeftCam != null ? stereo.LeftCam.farClipPlane : next.GameplayFarClipMeters;
                bool menuLike = currentFar <= 300f && currentFar < next.GameplayFarClipMeters * 0.5f;
                stereo.SetFarClipDistance(menuLike ? next.MenuFarClipMeters : next.GameplayFarClipMeters);
            }

            _current = next.Clone();
            if (persist)
                SaveToDisk(_settingsPath, _current);

            if (!_current.PerfLogEnabled)
                ClosePerfWriter();

            MelonLogger.Warning(
                "[V163 Settings][Apply] DLSS=native-menu " +
                $"OF={_current.OpticalFlowEnabled}@{_current.OpticalFlowResolutionScale:F3} " +
                $"renderScaleConfig={_current.RenderResolutionScale:F2} live={StereoRender.RenderResolutionScale:F2}" +
                (deferRenderScaleToRestart ? " RESTART_REQUIRED" : "") +
                $" clip(menu/game)={_current.MenuFarClipMeters:F0}/{_current.GameplayFarClipMeters:F0}m " +
                $"guardMaxDZ={_current.DepthGuardMaxToleranceMeters:F2}m mode={(_current.GameplayAerEnabled ? "AER" : "2E")} " +
                $"actual={(stereo != null ? stereo.GetActualRenderModeV112() : "2E/PRE")} nativeHUD=WORLD_PLANE " +
                $"stats={_current.StatsOverlayEnabled} perfLog={_current.PerfLogEnabled}.");

            if (persist && _current.PerfLogEnabled)
            {
                WritePerfEventAndFlush(
                    "SETTINGS_APPLY_END " +
                    $"renderScaleConfig={_current.RenderResolutionScale:F2} liveScale={StereoRender.RenderResolutionScale:F2}" +
                    (deferRenderScaleToRestart ? " restartRequired=1" : " restartRequired=0"),
                    stereo, player);
            }
        }

        private static void ApplyStaticFields(Settings s)
        {
            // V163: never override DLSS from our INI; native game OPTIONS - GRAPHICS owns it.
            SonsVRMod.V91NvofaResolutionScale = s.OpticalFlowResolutionScale;
            StereoRender.AerTemporalOpticalFlowV90Enabled = s.OpticalFlowEnabled;
            StereoRender.RenderResolutionScale = s.RenderResolutionScale;
            StereoRender.GameplayFarClipMeters = s.GameplayFarClipMeters;
            StereoRender.AerV105FillDepthMaxToleranceMeters = s.DepthGuardMaxToleranceMeters;
            StereoRender.UserGameplayAERV112 = s.GameplayAerEnabled;
            StereoRender.AerV104DepthGuardedFillPermanentEnabled = s.AerSpatialHoleFillEnabled;

            VRPlayer.TarpFarClipEnabled = true;
            VRPlayer.TarpFarClipMeters = s.MenuFarClipMeters;
            VRPlayer.GrabBagFarClipEnabled = true;
            VRPlayer.GrabBagFarClipMeters = s.MenuFarClipMeters;
        }

        private static void Sanitize(Settings s)
        {
            if (s == null) return;
            s.OpticalFlowResolutionScale = Mathf.Clamp(s.OpticalFlowResolutionScale, 0.25f, 1.0f);
            s.RenderResolutionScale = Mathf.Clamp(s.RenderResolutionScale, 0.50f, 1.30f);
            s.GameplayFarClipMeters = Mathf.Clamp(s.GameplayFarClipMeters, 100f, 24000f);
            s.MenuFarClipMeters = Mathf.Clamp(s.MenuFarClipMeters, 20f, 500f);
            s.SettingsOverlayScale = Mathf.Clamp(s.SettingsOverlayScale, 0.20f, 3.00f);
            s.StatsOverlayScale = Mathf.Clamp(s.StatsOverlayScale, 0.20f, 3.00f);
            if (Array.IndexOf(ShortcutKeys, s.KeyboardShortcutKey) < 0)
                s.KeyboardShortcutKey = "F10";
            s.DepthGuardMaxToleranceMeters = Mathf.Clamp(s.DepthGuardMaxToleranceMeters, 0.30f, 20f);
            s.PerfWarningFrameTimeMs = Mathf.Clamp(s.PerfWarningFrameTimeMs, 15f, 250f);
        }

        private static void UpdateCompositorTiming()
        {
            if (Time.unscaledTime < _nextStatsUpdate) return;
            _nextStatsUpdate = Time.unscaledTime + 0.25f;
            try
            {
                if (OpenVR.Compositor == null) return;
                Compositor_FrameTiming timing = new Compositor_FrameTiming();
                timing.m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming));
                if (OpenVR.Compositor.GetFrameTiming(ref timing, 0))
                {
                    _clientFrameMs = timing.m_flClientFrameIntervalMs;
                    _gpuMs = timing.m_flTotalRenderGpuMs;
                    _droppedFrames = timing.m_nNumDroppedFrames;
                    _reprojectionFlags = timing.m_nReprojectionFlags;
                }
            }
            catch { }
        }

        private static void UpdateStatsOverlay(StereoRender stereo)
        {
            if (!_current.StatsOverlayEnabled)
            {
                if (_statsGo != null && _statsGo.activeSelf) _statsGo.SetActive(false);
                return;
            }
            if (stereo == null || stereo.Head == null) return;
            EnsureStatsOverlay(stereo);
            // V137: right edge stays u=0.758 (V116), additional width grows inward.
            // This accommodates three-digit timings without pushing the overlay off-view.
            // V163: pin the right edge (u=0.758), scale width inward.
            float overlayWidth = 0.276f * _current.StatsOverlayScale;
            float overlayCenter = Mathf.Max(overlayWidth * 0.5f + 0.015f,
                0.758f - overlayWidth * 0.5f); // keep 3x from clipping beyond left edge
            PlaceWorldCanvasAtStereoViewportV112(_statsGo, stereo,
                overlayCenter, 0.727f, overlayWidth, 1.25f);
            if (_statsText == null) return;

            // V137: sample the same HDCamera.actualWidth/Height used by V129's
            // render-boundary probe. Never display allocated eye RT size as DLSS input.
            if (Time.unscaledTime >= _nextStatsResolutionPoll)
            {
                _nextStatsResolutionPoll = Time.unscaledTime + 0.25f;
                try
                {
                    Camera eye = stereo.LeftCam;
                    HDCamera hd = eye != null ? HDCamera.GetOrCreate(eye, 0) : null;
                    if (hd != null && hd.actualWidth > 0 && hd.actualHeight > 0)
                    {
                        bool dlss = eye != null && eye.GetComponent<HDAdditionalCameraData>() != null &&
                            eye.GetComponent<HDAdditionalCameraData>().allowDeepLearningSuperSampling;
                        _statsResolutionText = (dlss ? "DLSS: " : "DLSS OFF: ") +
                            hd.actualWidth + " X " + hd.actualHeight;
                    }
                    else _statsResolutionText = "DLSS: -- X --";
                }
                catch { _statsResolutionText = "DLSS: -- X --"; }
            }
            float fps = 1000f / Mathf.Max(1f, _emaDtMs);
            string actualModeV112 = stereo.GetActualRenderModeV112();
            _statsText.text =
                $"{actualModeV112}     ~{fps:F1} FPS     frame {_emaDtMs:F1} ms\n" +
                $"SteamVR {_clientFrameMs:F1} ms     GPU {_gpuMs:F1} ms\n" +
                $"dropped {_droppedFrames}     reproj 0x{_reprojectionFlags:X}\n" +
                _statsResolutionText;
        }

        private static void EnsureStatsOverlay(StereoRender stereo)
        {
            if (_statsGo != null)
            {
                if (!_statsGo.activeSelf) _statsGo.SetActive(true);
                return;
            }

            _statsGo = new GameObject("SonsVR_V112_PerfOverlay");
            Canvas canvas = _statsGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 5900;
            RectTransform rt = _statsGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1380f, 470f); // V137: four lines, wider inward
            _statsGo.transform.localScale = Vector3.one * 0.00046f; // V112 fallback only; normalized stereo viewport placement normally owns scale.

            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(_statsGo.transform, false);
            Image bg = bgGo.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.62f);
            RectTransform br = bgGo.GetComponent<RectTransform>();
            br.anchorMin = Vector2.zero; br.anchorMax = Vector2.one;
            br.offsetMin = Vector2.zero; br.offsetMax = Vector2.zero;

            GameObject textGo = new GameObject("PerfText");
            textGo.transform.SetParent(_statsGo.transform, false);
            _statsText = textGo.AddComponent<Text>();
            _statsText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _statsText.fontSize = 68;
            _statsText.alignment = TextAnchor.MiddleLeft;
            _statsText.horizontalOverflow = HorizontalWrapMode.Overflow; // V137: never wrap 120 ms
            _statsText.verticalOverflow = VerticalWrapMode.Overflow;
            _statsText.color = new Color(1f, 0.90f, 0.12f, 1f);
            RectTransform tr = textGo.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(18f, 8f); tr.offsetMax = new Vector2(-18f, -8f);

            _statsGo.transform.SetParent(stereo.Head, false);
            _statsGo.transform.localPosition = new Vector3(0.30f, 0.20f, 1.25f); // fallback only
            _statsGo.transform.localRotation = Quaternion.identity;
            _statsGo.SetActive(true);
        }

        // V112: stop treating "top-right" as a guessed meter offset from Head. A world-space
        // Canvas lives in 3D and is then projected through two asymmetric eye frusta; x/y/z meters
        // are therefore not screen coordinates. This helper defines our custom overlays in
        // normalized stereo viewport space instead. The centre and requested width are unprojected
        // through BOTH real eye cameras at a chosen physical distance, averaged cyclopeanly, and
        // then converted to the Canvas's world scale. Render resolution changes no longer move it.
        private static bool TryStereoViewportWorldPointV112(
            StereoRender stereo, float u, float v, float forwardMeters, out Vector3 world)
        {
            world = Vector3.zero;
            if (stereo == null) return false;

            int count = 0;
            Camera[] cams = { stereo.LeftCam, stereo.RightCam };
            for (int i = 0; i < cams.Length; ++i)
            {
                Camera cam = cams[i];
                if (cam == null) continue;
                try
                {
                    Vector3 p = cam.ViewportToWorldPoint(new Vector3(
                        Mathf.Clamp01(u), Mathf.Clamp01(v), Mathf.Max(0.20f, forwardMeters)));
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                        float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                        continue;
                    world += p;
                    count++;
                }
                catch { }
            }

            if (count <= 0) return false;
            world /= count;
            return true;
        }

        private static void PlaceWorldCanvasAtStereoViewportV112(
            GameObject go, StereoRender stereo,
            float centerU, float centerV, float viewportWidth, float forwardMeters)
        {
            if (go == null || stereo == null || stereo.Head == null) return;
            RectTransform rt = go.GetComponent<RectTransform>();
            if (rt == null) return;

            Vector3 center;
            Vector3 left;
            Vector3 right;
            float half = Mathf.Clamp(viewportWidth * 0.5f, 0.01f, 1.5f); // V163 allows 3x viewport sizing
            if (!TryStereoViewportWorldPointV112(stereo, centerU, centerV, forwardMeters, out center) ||
                !TryStereoViewportWorldPointV112(stereo, centerU - half, centerV, forwardMeters, out left) ||
                !TryStereoViewportWorldPointV112(stereo, centerU + half, centerV, forwardMeters, out right))
                return;

            float pxWidth = Mathf.Abs(rt.rect.width) > 1f ? Mathf.Abs(rt.rect.width) :
                Mathf.Max(1f, Mathf.Abs(rt.sizeDelta.x));
            float worldWidth = Vector3.Distance(left, right);
            float scale = Mathf.Clamp(worldWidth / pxWidth, 0.00005f, 0.01f);

            if (go.transform.parent != stereo.Head)
                go.transform.SetParent(stereo.Head, false);

            go.transform.position = center;
            go.transform.rotation = Quaternion.LookRotation(stereo.Head.forward, stereo.Head.up);
            go.transform.localScale = Vector3.one * scale;
        }

        private static void UpdatePerfLogger(StereoRender stereo, VRPlayer player, float instantaneousDtMs)
        {
            if (!_current.PerfLogEnabled) return;
            EnsurePerfWriter();
            if (_perfWriter == null) return;

            float now = Time.unscaledTime;
            if (now >= _nextPerfSample)
            {
                _nextPerfSample = now + 0.50f; // 2 Hz steady-state log
                WritePerfRow("SAMPLE", stereo, player, instantaneousDtMs, "");
            }

            float spikeMs = Mathf.Max(instantaneousDtMs, _clientFrameMs);
            if (spikeMs >= _current.PerfWarningFrameTimeMs && now >= _nextPerfWarningAllowed)
            {
                _nextPerfWarningAllowed = now + 0.75f;
                string note = $"frame time exceeded {_current.PerfWarningFrameTimeMs:F0}ms (observed {spikeMs:F1}ms)";
                WritePerfRow("WARNING", stereo, player, instantaneousDtMs, note);
                // Rare warnings are diagnostic breadcrumbs. Flush immediately so a subsequent
                // native hang/process kill cannot erase the last <=5 seconds of buffered context.
                FlushPerfWriterBestEffort();
            }

            if (now >= _nextPerfFlush)
            {
                _nextPerfFlush = now + 5f;
                try { _perfWriter.Flush(); } catch { }
            }
        }

        private static void WritePerfEventAndFlush(string note, StereoRender stereo, VRPlayer player)
        {
            // For an APPLY event, force-open the writer if either the current or requested
            // configuration wants logging. Callers use this only for explicit user actions.
            if (_perfWriter == null)
                EnsurePerfWriter();
            if (_perfWriter == null)
                return;

            WritePerfRow("EVENT", stereo, player, Time.unscaledDeltaTime * 1000f, note);
            FlushPerfWriterBestEffort();
        }

        private static void FlushPerfWriterBestEffort()
        {
            try { _perfWriter?.Flush(); } catch { }
        }

        private static void EnsurePerfWriter()
        {
            if (_perfWriter != null) return;
            try
            {
                string dir = Path.GetDirectoryName(_settingsPath);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                _perfPath = MakeUniquePerfPath(dir, stamp);
                FileStream fs = new FileStream(_perfPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _perfWriter = new StreamWriter(fs, new UTF8Encoding(false), 64 * 1024);
                _perfWriter.WriteLine("type,wall_time,unity_time,frame,scene,context,render_mode,unity_dt_ms,ema_dt_ms,approx_fps,steamvr_client_ms,gpu_ms,dropped,reproj_flags,far_clip_m,render_scale,dlss,dlss_quality,of,of_linear_scale,guard_max_dz_m,note");
                _nextPerfFlush = Time.unscaledTime + 5f;
                MelonLogger.Msg($"[V105 Perf] Lightweight 2-Hz log: {_perfPath}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V105 Perf] Could not open log: " + e.Message);
                _perfWriter = null;
            }
        }

        private static string MakeUniquePerfPath(string dir, string stamp)
        {
            string basePath = Path.Combine(dir, "SonsVR_Perf_" + stamp + ".csv");
            if (!File.Exists(basePath)) return basePath;
            for (int i = 1; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, $"SonsVR_Perf_{stamp}_{i:D3}.csv");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, "SonsVR_Perf_" + stamp + "_" + Guid.NewGuid().ToString("N") + ".csv");
        }

        private static void ClosePerfWriter()
        {
            try { _perfWriter?.Flush(); _perfWriter?.Dispose(); } catch { }
            _perfWriter = null;
        }

        private static void WritePerfRow(string type, StereoRender stereo, VRPlayer player, float dtMs, string note)
        {
            try
            {
                string scene = SceneManager.GetActiveScene().name ?? "";
                string context = player != null ? player.GetPerformanceContextV105() : "NO_VRPLAYER";
                float clip = stereo != null && stereo.LeftCam != null ? stereo.LeftCam.farClipPlane : 0f;
                float fps = 1000f / Mathf.Max(1f, _emaDtMs);
                _perfWriter.WriteLine(string.Join(",", new string[]
                {
                    Csv(type),
                    Csv(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    Time.unscaledTime.ToString("F3", CultureInfo.InvariantCulture),
                    Time.frameCount.ToString(CultureInfo.InvariantCulture),
                    Csv(scene), Csv(context),
                    Csv(stereo != null ? stereo.GetActualRenderModeV112() : "2E/PRE"),
                    dtMs.ToString("F3", CultureInfo.InvariantCulture),
                    _emaDtMs.ToString("F3", CultureInfo.InvariantCulture),
                    fps.ToString("F2", CultureInfo.InvariantCulture),
                    _clientFrameMs.ToString("F3", CultureInfo.InvariantCulture),
                    _gpuMs.ToString("F3", CultureInfo.InvariantCulture),
                    _droppedFrames.ToString(CultureInfo.InvariantCulture),
                    "0x" + _reprojectionFlags.ToString("X"),
                    clip.ToString("F1", CultureInfo.InvariantCulture),
                    StereoRender.RenderResolutionScale.ToString("F2", CultureInfo.InvariantCulture),
                    "NATIVE", "NATIVE",
                    _current.OpticalFlowEnabled ? "1" : "0",
                    _current.OpticalFlowResolutionScale.ToString("F3", CultureInfo.InvariantCulture),
                    _current.DepthGuardMaxToleranceMeters.ToString("F3", CultureInfo.InvariantCulture),
                    Csv(note)
                }));
            }
            catch { }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string ResolveSettingsPath()
        {
            try
            {
                string asm = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asm))
                    return Path.Combine(Path.GetDirectoryName(asm), "SonsVR_Settings.ini");
            }
            catch { }
            return Path.Combine(Directory.GetCurrentDirectory(), "Mods", "SonsVR_Settings.ini");
        }

        private static Settings LoadFromDisk(string path)
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(path)) return s;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    float f;
                    bool b;
                    switch (key.ToLowerInvariant())
                    {
                        case "opticalflowenabled": if (bool.TryParse(val, out b)) s.OpticalFlowEnabled = b; break;
                        case "opticalflowresolutionscale": if (TryFloat(val, out f)) s.OpticalFlowResolutionScale = f; break;
                        case "renderresolutionscale": if (TryFloat(val, out f)) s.RenderResolutionScale = f; break;
                        case "gameplayfarclipmeters": if (TryFloat(val, out f)) s.GameplayFarClipMeters = f; break;
                        case "menufarclipmeters": if (TryFloat(val, out f)) s.MenuFarClipMeters = f; break;
                        case "depthguardmaxtolerancemeters": if (TryFloat(val, out f)) s.DepthGuardMaxToleranceMeters = f; break;
                        case "gameplayaerenabled": if (bool.TryParse(val, out b)) s.GameplayAerEnabled = b; break;
                        case "aerspatialholefillenabled": if (bool.TryParse(val, out b)) s.AerSpatialHoleFillEnabled = b; break;
                        case "centerhudvertically": if (bool.TryParse(val, out b)) s.CenterHudVertically = b; break;
                        case "statsoverlayenabled": if (bool.TryParse(val, out b)) s.StatsOverlayEnabled = b; break;
                        case "perflogenabled": if (bool.TryParse(val, out b)) s.PerfLogEnabled = b; break;
                        case "perfwarningframetimems": if (TryFloat(val, out f)) s.PerfWarningFrameTimeMs = f; break;
                        case "settingsoverlayscale": if (TryFloat(val, out f)) s.SettingsOverlayScale = f; break;
                        case "statsoverlayscale": if (TryFloat(val, out f)) s.StatsOverlayScale = f; break;
                        case "keyboardshortcutkey": s.KeyboardShortcutKey = val; break;
                        case "legacyvrgesturesenabled": if (bool.TryParse(val, out b)) s.LegacyVrGesturesEnabled = b; break;
                    }
                }
                return s;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V105 Settings] Failed reading settings; defaults used: " + e.Message);
                return new Settings();
            }
        }

        private static bool TryFloat(string s, out float f)
        {
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ||
                   float.TryParse(s, out f);
        }

        private static void SaveToDisk(string path, Settings s)
        {
            try
            {
                StringBuilder b = new StringBuilder();
                b.AppendLine("# SonsVR V168G runtime settings; automatically migrated on update");
                b.AppendLine("SettingsSchemaVersion=168");
                b.AppendLine("# Gamepad View+Menu or keyboard Ctrl+Alt+KeyboardShortcutKey opens the VR panel.");
                b.AppendLine("# DLSS is controlled only in the game's OPTIONS - GRAPHICS menu.");
                b.AppendLine("OpticalFlowEnabled=" + s.OpticalFlowEnabled);
                b.AppendLine("OpticalFlowResolutionScale=" + s.OpticalFlowResolutionScale.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("RenderResolutionScale=" + s.RenderResolutionScale.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("GameplayFarClipMeters=" + s.GameplayFarClipMeters.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("MenuFarClipMeters=" + s.MenuFarClipMeters.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("DepthGuardMaxToleranceMeters=" + s.DepthGuardMaxToleranceMeters.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("GameplayAerEnabled=" + s.GameplayAerEnabled);
                b.AppendLine("# V141 AER: False=Depth-grid no spatial fill (recommended); True=V103 guarded-fill rollback.");
                b.AppendLine("AerSpatialHoleFillEnabled=" + s.AerSpatialHoleFillEnabled);
                b.AppendLine("CenterHudVertically=" + s.CenterHudVertically);
                b.AppendLine("StatsOverlayEnabled=" + s.StatsOverlayEnabled);
                b.AppendLine("PerfLogEnabled=" + s.PerfLogEnabled);
                b.AppendLine("PerfWarningFrameTimeMs=" + s.PerfWarningFrameTimeMs.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("SettingsOverlayScale=" + s.SettingsOverlayScale.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("StatsOverlayScale=" + s.StatsOverlayScale.ToString("0.###", CultureInfo.InvariantCulture));
                b.AppendLine("KeyboardShortcutKey=" + s.KeyboardShortcutKey);
                b.AppendLine("# V168G: next-launch only; may conflict with Xbox gamepad prompts/input when ON.");
                b.AppendLine("LegacyVrGesturesEnabled=" + s.LegacyVrGesturesEnabled);
                File.WriteAllText(path, b.ToString());
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[V105 Settings] Failed saving settings: " + e.Message);
            }
        }
    }
}
