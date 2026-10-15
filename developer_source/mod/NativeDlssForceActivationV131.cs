using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SonsVRMod
{
    /// <summary>
    /// V131: One-shot 2E HDRP DLSS-activation experiment, deliberately risky.
    /// The HDRP14 DynamicResUpscaleFilter enum does NOT contain DLSS. Its TAAU
    /// value is the ordinary fallback filter, not proof of the executed upscaler.
    /// The real HDRP14 asset switch is GlobalDynamicResolutionSettings.enableDLSS.
    ///
    /// Schedule, anchored to successful gameplay receiver routing AND HDRP CopyTo:
    ///    0..30 seconds   : passive V127/V129/V130 truth capture
    ///   30..35 seconds   : five-second visible in-HMD warning
    ///   >=35 seconds    : remove warning; allow two complete frames; flush logs;
    ///                     attempt one supported settings write, then monitor
    /// No forcing of resolution fraction, generic filter enum, scaler slot,
    /// camera render target, native menu quality, NVOFA or AER reconstruction.
    /// </summary>
    internal static class NativeDlssForceActivationV131
    {
        internal static bool CueActive => _phase >= 1; // keep overlay updater alive to consume the final empty cue
        private const float BaselineSeconds = 30f;
        private const float WarningSeconds = 5f;
        private static readonly FieldInfo HdrpCopyField = typeof(StereoRender).GetField(
            "_hdrpDataCopiedOnce", BindingFlags.Instance | BindingFlags.NonPublic);
        private static int _phase; // 0 wait; 1 baseline; 2 warning; 3 clear; 4 attempted; 5 finished
        private static float _startedAt;
        private static float _warningAt;
        private static float _nextPoll;
        private static float _nextCheck;
        private static int _lastWarnSecond = -1;
        private static int _cueClearedFrame;
        private static bool _changed;
        private static bool _originalEnableDlss;
        private static string _lastWriteSurface = "none";
        private static HDRenderPipelineAsset _asset;
        private static int _reportCount;

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.10f;
            try
            {
                if (StereoRender.UserGameplayAERV112 || !NativeDlssReceiverRouterV128.Routed)
                {
                    if (_phase == 1 || _phase == 2 || _phase == 3)
                    {
                        ClearCue();
                        _phase = 6; // permanently abandon the hazardous one-shot run
                        MelonLogger.Warning("[V131 DLSS-ACT] ABORT before activation: AER / route inactive; will not retry this session.");
                    }
                    else if (_phase == 4 || _phase == 5)
                    {
                        Restore("AER or leaving routed gameplay");
                        _phase = 5;
                    }
                    return;
                }
                StereoRender stereo = VRPlayer.Instance != null ? VRPlayer.Instance.StereoRender : null;
                if (stereo == null || stereo.LeftCam == null || stereo.RightCam == null ||
                    HdrpCopyField == null || !(HdrpCopyField.GetValue(stereo) is bool copied) || !copied)
                    return;
                if (_phase == 0)
                {
                    _startedAt = now;
                    _phase = 1;
                    MelonLogger.Warning("[V131 DLSS-ACT] BASELINE START: two eyes routed; HDRP gameplay copy complete; 30s read-only capture.");
                    NativeDlssLiveResolutionTraceV129.V131FlushMarker("BASELINE_START");
                    return;
                }
                if (_phase == 1 && now - _startedAt >= BaselineSeconds)
                {
                    NativeDlssLiveResolutionTraceV129.V131FlushMarker("BASELINE_END_WARNING_START");
                    MelonLogger.Warning("[V131 DLSS-ACT] BASELINE COMPLETE. WARNING FIVE SECONDS before HDRP asset rebuild.");
                    _warningAt = now;
                    _phase = 2;
                    _lastWarnSecond = -1;
                }
                if (_phase == 2)
                {
                    float remain = WarningSeconds - (now - _warningAt);
                    if (remain > 0f)
                    {
                        int seconds = Mathf.CeilToInt(remain);
                        if (seconds != _lastWarnSecond)
                        {
                            _lastWarnSecond = seconds;
                            StereoRender.SetAerV102HmdCue("V131 DLSS PIPELINE EXPERIMENT\n" +
                                "OVERRIDE IN " + seconds + " SECONDS\nBRACE FOR CHANGE / POSSIBLE CRASH");
                        }
                        return;
                    }
                    ClearCue();
                    _cueClearedFrame = Time.frameCount;
                    _phase = 3;
                    MelonLogger.Warning("[V131 DLSS-ACT] WARNING ENDED; cue cleared, waiting two frames before asset write.");
                    return;
                }
                if (_phase == 3)
                {
                    if (Time.frameCount < _cueClearedFrame + 2) return;
                    NativeDlssLiveResolutionTraceV129.V131FlushMarker("PRE_FORCE_FLUSHED");
                    MelonLogger.Warning("[V131 DLSS-ACT] ABOUT TO ATTEMPT HDRP ASSET enableDLSS=true; writable access is version-dependent. Baseline TSV already flushed.");
                    _phase = 4; // never retry a failed or crashing write
                    ForceOnce();
                    _nextCheck = Time.unscaledTime + 2f;
                    _phase = 5;
                    return;
                }
                if (_phase == 5 && now >= _nextCheck && _reportCount < 6)
                {
                    _nextCheck = now + 4f;
                    _reportCount++;
                    ReportAfter(stereo);
                }
            }
            catch (Exception e)
            {
                ClearCue();
                MelonLogger.Warning("[V131 DLSS-ACT] ERROR " + e.GetType().Name + ": " + e.Message);
                NativeDlssLiveResolutionTraceV129.V131FlushMarker("EXCEPTION");
                _phase = 5;
            }
        }

        private static void ForceOnce()
        {
            // V131B: the game's generated IL2CPP wrapper exposes a GETTER-ONLY
            // currentPlatformRenderPipelineSettings. Do not assign to it in C#.
            // Instead, discover a genuinely writable IL2CPP-backed member.
            var source = GraphicsSettings.currentRenderPipeline;
            _asset = source != null ? source.TryCast<HDRenderPipelineAsset>() : null;
            if (_asset == null)
            {
                MelonLogger.Warning("[V131 DLSS-ACT] NO_WRITE: current HDRP asset not found.");
                NativeDlssLiveResolutionTraceV129.V131FlushMarker("ASSET_NOT_FOUND");
                return;
            }

            var drs = _asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
            _originalEnableDlss = drs.enableDLSS;
            MelonLogger.Warning("[V131 DLSS-ACT] ASSET BEFORE: enabled=" + drs.enabled +
                " enableDLSS=" + drs.enableDLSS +
                " forceResolution=" + drs.forceResolution +
                " forcedPercentage=" + drs.forcedPercentage +
                " min=" + drs.minPercentage + " max=" + drs.maxPercentage +
                " fallbackFilter=" + drs.upsampleFilter +
                " DLSSOptimal=" + drs.DLSSUseOptimalSettings +
                " quality=" + drs.DLSSPerfQualitySetting);

            NativeDlssLiveResolutionTraceV129.V131FlushMarker("HDRP_ASSET_WRITE_BEGIN");
            _changed = !_originalEnableDlss; // mark before a potentially partial native write
            string detail;
            if (!TryWriteAssetDlss(true, out detail))
            {
                MelonLogger.Warning("[V131 DLSS-ACT] NO_WRITE_UNSUPPORTED: " + detail +
                    "; game's getter-only wrapper has no verified writable route. " +
                    "No pipeline rebuild or DLSS activation claimed.");
                NativeDlssLiveResolutionTraceV129.V131FlushMarker("NO_WRITE_UNSUPPORTED");
                return;
            }
            var after = _asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
            MelonLogger.Warning("[V131 DLSS-ACT] ASSET WRITE RETURNED: before=" + _originalEnableDlss +
                " after=" + after.enableDLSS + " via=" + _lastWriteSurface +
                " fallbackFilter=" + after.upsampleFilter +
                " (asset field write does NOT itself prove pipeline rebuild or NVIDIA execution). " +
                (_originalEnableDlss ? "DLSS was already enabled: identical-value write only." :
                                       "Asset flag changed FALSE -> TRUE."));
            NativeDlssLiveResolutionTraceV129.V131FlushMarker("HDRP_ASSET_WRITE_RETURNED");
        }

        private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic |
                                                 BindingFlags.Instance;

        private static bool TryWriteStructMember(object boxed, Type expectedType,
            string[] names, object value, out string via)
        {
            via = "no writable member";
            Type t = boxed.GetType();
            foreach (string name in names)
            {
                PropertyInfo p = t.GetProperty(name, MemberFlags);
                if (p != null && p.PropertyType == expectedType && p.GetSetMethod(true) != null &&
                    p.GetIndexParameters().Length == 0)
                {
                    p.SetValue(boxed, value, null);
                    via = t.Name + "." + p.Name + " [property]";
                    return true;
                }
                FieldInfo f = t.GetField(name, MemberFlags);
                if (f != null && f.FieldType == expectedType && !f.IsInitOnly)
                {
                    f.SetValue(boxed, value);
                    via = t.Name + "." + f.Name + " [field]";
                    return true;
                }
            }
            return false;
        }

        private static bool TryWriteAssetDlss(bool newValue, out string detail)
        {
            detail = "unknown";
            if (_asset == null) { detail = "asset=null"; return false; }

            // Make the two value-type copies explicit. The original code attempted
            // to mutate the return value of a property (CS1612).
            RenderPipelineSettings current = _asset.currentPlatformRenderPipelineSettings;
            GlobalDynamicResolutionSettings drs = current.dynamicResolutionSettings;
            drs.enableDLSS = newValue;
            object boxedSettings = current;
            string nestedMember;
            if (!TryWriteStructMember(boxedSettings, typeof(GlobalDynamicResolutionSettings),
                    new[] { "dynamicResolutionSettings", "m_DynamicResolutionSettings" },
                    drs, out nestedMember))
            {
                detail = "RenderPipelineSettings has no writable dynamicResolutionSettings field/property";
                return false;
            }

            // The public currentPlatformRenderPipelineSettings setter is absent
            // in the game's IL2CPP-generated wrapper (CS0200). We only use
            // members actually present in this runtime, NEVER guessed offsets.
            string assetMember;
            if (!TryWriteStructMember(_asset, typeof(RenderPipelineSettings),
                    new[] { "currentPlatformRenderPipelineSettings", "m_RenderPipelineSettings",
                            "renderPipelineSettings" }, boxedSettings, out assetMember))
            {
                detail = "HDRenderPipelineAsset exposes no writable pipeline-settings member " +
                    "(nested copy was available through " + nestedMember + ")";
                return false;
            }

            _lastWriteSurface = assetMember + " / " + nestedMember;
            var verified = _asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
            if (verified.enableDLSS != newValue)
            {
                detail = "read-back did not reflect requested enableDLSS=" + newValue +
                    " via " + _lastWriteSurface + "; observed=" + verified.enableDLSS;
                return false;
            }
            detail = "read-back verified via " + _lastWriteSurface;
            return true;
        }

        private static void ReportAfter(StereoRender stereo)
        {
            try
            {
                var assetDrs = _asset != null ? _asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings : default(GlobalDynamicResolutionSettings);
                var lh = HDCamera.GetOrCreate(stereo.LeftCam, 0);
                var rh = HDCamera.GetOrCreate(stereo.RightCam, 0);
                MelonLogger.Warning("[V131 DLSS-ACT] AFTER +" + _reportCount +
                    " enableDLSS=" + (_asset != null ? assetDrs.enableDLSS.ToString() : "no asset") +
                    " LH=" + (lh != null ? lh.actualWidth + "x" + lh.actualHeight : "NA") +
                    " RH=" + (rh != null ? rh.actualWidth + "x" + rh.actualHeight : "NA") +
                    " (V129 TSV has frame-scoped input + DRS filter truth)");
                NativeDlssLiveResolutionTraceV129.V131FlushMarker("POST_OVERRIDE_" + _reportCount);
            }
            catch (Exception e) { MelonLogger.Warning("[V131 DLSS-ACT] AFTER read failed: " + e.Message); }
        }

        private static void ClearCue()
        {
            StereoRender.SetAerV102HmdCue("");
        }

        internal static void Restore(string reason)
        {
            ClearCue();
            if (!_changed || _asset == null) return;
            _changed = false; // never fight repeated native failures
            try
            {
                var drs = _asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings;
                if (drs.enableDLSS != _originalEnableDlss)
                {
                    string detail;
                    bool restored = TryWriteAssetDlss(_originalEnableDlss, out detail);
                    MelonLogger.Warning("[V131 DLSS-ACT] RESTORE " +
                        (restored ? "verified" : "FAILED") + " enableDLSS=" +
                        _originalEnableDlss + " reason=" + reason + " " + detail);
                }
                else
                {
                    MelonLogger.Warning("[V131 DLSS-ACT] RESTORE skipped: original DLSS state unchanged (" + reason + ").");
                }
            }
            catch (Exception e) { MelonLogger.Warning("[V131 DLSS-ACT] RESTORE failed: " + e.Message); }
        }
    }
}
