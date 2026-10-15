using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildDepthThingy
{
    private const string ShaderAssetPath = "Assets/DepthStereoWarp.shader";
    private const string HudDepthProxyShaderAssetPathV72 = "Assets/HudDepthProxyV72.shader";
    private const string TemporalScatterComputeAssetPathV88 = "Assets/TemporalScatterV89.compute";
    private const string OpticalFlowLumaShaderAssetPathV90 = "Assets/OpticalFlowLumaV90.shader";
    private const string AerForensicsComputeAssetPathV99 = "Assets/AerForensicsV99.compute";
    private const string BundleName = "sonsvr_depththingy";

    [MenuItem("SonsVR/Build V101 Sky/Depth Forensics THINGY")]
    public static void Build()
    {
        BuildInternal();
    }

    // Used by BUILD_V51.bat so the secretary can compile the shader bundle + mod DLL
    // with one deterministic command instead of hand-copying potatoes between folders.
    public static void BuildBatch()
    {
        BuildInternal();
    }

    private static void ValidateShaderOrThrow(string assetPath, string[] requiredPasses)
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
        if (shader == null)
            throw new Exception("Shader asset could not be loaded: " + assetPath);

        if (ShaderUtil.ShaderHasError(shader))
        {
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                Debug.LogError(
                    "[SonsVR ShaderValidation] " + assetPath + " :: " +
                    message.severity + " :: " + message.message);

            throw new Exception(
                "Shader has compiler errors: " + assetPath +
                ". See shader_build.log.");
        }

        Material material = new Material(shader);
        try
        {
            foreach (string passName in requiredPasses)
            {
                int pass = material.FindPass(passName);
                if (pass < 0)
                    throw new Exception(
                        "Required shader pass missing: " + assetPath + " :: " +
                        passName + " (passCount=" + shader.passCount + ")");
            }

            Debug.Log(
                "[SonsVR ShaderValidation] OK " + assetPath +
                " passCount=" + shader.passCount);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(material);
        }
    }


    private static void ValidateComputeOrThrow(string assetPath, string[] requiredKernels)
    {
        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(assetPath);
        if (shader == null)
            throw new Exception("Compute shader asset could not be loaded: " + assetPath);

        foreach (string kernelName in requiredKernels)
        {
            if (!shader.HasKernel(kernelName))
                throw new Exception(
                    "Required compute kernel missing: " + assetPath + " :: " + kernelName);

            int kernel = shader.FindKernel(kernelName);
            if (kernel < 0)
                throw new Exception(
                    "Compute kernel lookup failed: " + assetPath + " :: " + kernelName);
        }

        Debug.Log(
            "[SonsVR ShaderValidation] OK " + assetPath +
            " kernels=[" + string.Join(",", requiredKernels) + "]");
    }

    private static void BuildInternal()
    {
        string outputDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../THINGY_OUT"));
        Directory.CreateDirectory(outputDir);

        string bundlePath = Path.Combine(outputDir, BundleName);
        if (File.Exists(bundlePath))
            File.Delete(bundlePath);

        ValidateShaderOrThrow(
            ShaderAssetPath,
            new[]
            {
                "StereoWarp",
                "HoleFill",
                "TargetEyeHoleFallback",
                "ConfidenceDebug",
                "CaptureEyeDepth",
                "DebugDepthLocal2D",
                "DebugDepthGlobalMip0",
                "PerPixelScatterResolveV88"
            });

        ValidateComputeOrThrow(
            TemporalScatterComputeAssetPathV88,
            new[] { "ClearScatter", "SplatDepth", "ResolveSource" });

        ValidateShaderOrThrow(
            OpticalFlowLumaShaderAssetPathV90,
            new[] { "OpticalFlowLumaV90" });

        ValidateComputeOrThrow(
            AerForensicsComputeAssetPathV99,
            new[] { "BlockMatch", "TileStats", "WarpField", "MotionField", "OpticalFlowField", "DepthField", "CoverageField", "SceneStructureField", "DepthClassStatsV101", "SkyRayClassFieldV101", "OcclusionClassStatsV102", "OcclusionComplexityFieldV102", "DepthColorRiskFieldV102" });

        ValidateShaderOrThrow(
            HudDepthProxyShaderAssetPathV72,
            new[]
            {
                "HudDepthProxy",
                "HudDepthProxyExactMeshV73",
                "HudDensePlanarRemapV76",
                "DepthInputForensicsV80",
                "V81CameraLocalDepthCaptureArray",
                "V81CameraLocalDepthDiagArray",
                "V81CameraLocalDepthCapture2D",
                "V81CameraLocalDepthDiag2D",
                "V89CameraMotionCaptureArray",
                "V89CameraMotionCapture2D"
            });

        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { ShaderAssetPath, HudDepthProxyShaderAssetPathV72, TemporalScatterComputeAssetPathV88, OpticalFlowLumaShaderAssetPathV90, AerForensicsComputeAssetPathV99 }
        };

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outputDir,
            new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64
        );

        if (manifest == null)
            throw new Exception("AssetBundle build returned null manifest; see the Unity Console/Editor.log.");

        if (!File.Exists(bundlePath))
            throw new Exception("AssetBundle build reported completion but fresh bundle file is missing: " + bundlePath);

        long bytes = new FileInfo(bundlePath).Length;
        if (bytes < 1024)
            throw new Exception("Fresh bundle is suspiciously tiny (" + bytes + " bytes): " + bundlePath);

        Debug.Log("[SonsVR V101] Depth + HUD + scatter + NVOFA + sky/depth forensic compute THINGY built successfully: " + bundlePath + " (" + bytes + " bytes)");

        if (!Application.isBatchMode)
            EditorUtility.RevealInFinder(bundlePath);
    }
}
