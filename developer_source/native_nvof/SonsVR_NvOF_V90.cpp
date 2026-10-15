// SonsVR V90 - NVIDIA Optical Flow SDK 5.0.7 D3D11 bridge
//
// Purpose:
//   Run NVOFA entirely on GPU resources while interoperating with Unity D3D11 RTs.
//   Unity commonly exposes RenderTextures as TYPELESS ID3D11Texture2D resources
//   even when their shader view is R8_UNORM / R16G16_SINT. NVOFA requires typed
//   resources. V90d therefore lets NVOFA allocate its own typed R8_UNORM inputs
//   and R16G16_SINT output, then GPU-copies Unity's typeless-compatible resources
//   into/out of those proxies on the same D3D11 device/context. No CPU image copy.
//
//   A Unity GL.IssuePluginEvent calls OnRenderEvent on the render thread after the
//   current luminance blit.
//
// Important direction convention:
//   We deliberately execute CURRENT -> PREVIOUS.  NVOFA's forward output is then
//   anchored at CURRENT image pixels and points backwards in time.  The V90 HLSL
//   subtracts camera-only backward motion and negates the residual to extrapolate
//   current -> predicted-next.  This avoids needing the API's bidirectional mode.
//
// The NVIDIA wrapper sources bundled beside this file are copied verbatim from
// Optical Flow SDK 5.0.7 and retain NVIDIA's original license headers.

#define NOMINMAX
#include <Windows.h>
#include <d3d11.h>
#include <wrl.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "Common/NvOFBase/NvOFD3D11.h"

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr int kEyeCount = 2;
    constexpr int kEventBase = 0x5690;

    struct EyeState
    {
        // Unity-owned resources. Their *resource* format may be TYPELESS even
        // though Unity exposes a typed shader view. We never register these with
        // NVOFA directly; they are copy endpoints only.
        ComPtr<ID3D11Texture2D> input[2];
        ComPtr<ID3D11Texture2D> output;
        DXGI_FORMAT unityInputFormat[2] = { DXGI_FORMAT_UNKNOWN, DXGI_FORMAT_UNKNOWN };
        DXGI_FORMAT unityOutputFormat = DXGI_FORMAT_UNKNOWN;

        uint32_t width = 0;
        uint32_t height = 0;
        bool configured = false;

        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;

        // NVOFA-owned, *typed* D3D11 resources allocated by the SDK wrapper.
        NvOFObj session;
        NvOFBufferObj inputBuffer[2];
        NvOFBufferObj outputBuffer;
        DXGI_FORMAT proxyInputFormat[2] = { DXGI_FORMAT_UNKNOWN, DXGI_FORMAT_UNKNOWN };
        DXGI_FORMAT proxyOutputFormat = DXGI_FORMAT_UNKNOWN;

        int pendingCurrentSlot = -1;
        int pendingFrame = -1;
        uint64_t pendingSerial = 0;

        int completedFrame = -1;
        int completedCurrentSlot = -1;
        uint64_t executeCount = 0;
        double lastApiCallMs = 0.0;
        int stateCode = 0; // 0=empty,1=configured,2=ready,3=executed,-1=error
        int lastErrorCode = 0;
        std::string lastError;

        void ResetSessionOnly()
        {
            outputBuffer.reset();
            inputBuffer[1].reset();
            inputBuffer[0].reset();
            session.reset();
            context.Reset();
            device.Reset();
            proxyInputFormat[0] = DXGI_FORMAT_UNKNOWN;
            proxyInputFormat[1] = DXGI_FORMAT_UNKNOWN;
            proxyOutputFormat = DXGI_FORMAT_UNKNOWN;
            completedFrame = -1;
            completedCurrentSlot = -1;
            stateCode = configured ? 1 : 0;
        }

        void ResetAll()
        {
            ResetSessionOnly();
            input[0].Reset();
            input[1].Reset();
            output.Reset();
            unityInputFormat[0] = DXGI_FORMAT_UNKNOWN;
            unityInputFormat[1] = DXGI_FORMAT_UNKNOWN;
            unityOutputFormat = DXGI_FORMAT_UNKNOWN;
            width = height = 0;
            configured = false;
            pendingCurrentSlot = -1;
            pendingFrame = -1;
            pendingSerial = 0;
            executeCount = 0;
            lastApiCallMs = 0.0;
            stateCode = 0;
            lastErrorCode = 0;
            lastError.clear();
        }
    };

    std::mutex g_mutex;
    EyeState g_eye[kEyeCount];

    void SetError(EyeState& s, int code, const std::string& message)
    {
        s.stateCode = -1;
        s.lastErrorCode = code;
        s.lastError = message;
    }

    bool IsR8CopyFamily(DXGI_FORMAT f)
    {
        return f == DXGI_FORMAT_R8_TYPELESS || f == DXGI_FORMAT_R8_UNORM;
    }

    bool IsShort2CopyFamily(DXGI_FORMAT f)
    {
        return f == DXGI_FORMAT_R16G16_TYPELESS || f == DXGI_FORMAT_R16G16_SINT;
    }

    bool ValidateUnityTexture(
        ID3D11Texture2D* tex,
        uint32_t width,
        uint32_t height,
        bool inputTexture,
        const char* label,
        DXGI_FORMAT& actualFormat,
        std::string& error)
    {
        actualFormat = DXGI_FORMAT_UNKNOWN;
        if (!tex)
        {
            error = std::string(label) + " pointer is null";
            return false;
        }

        D3D11_TEXTURE2D_DESC d{};
        tex->GetDesc(&d);
        actualFormat = d.Format;

        if (d.Width != width || d.Height != height)
        {
            char buf[320];
            std::snprintf(buf, sizeof(buf), "%s size mismatch: native=%ux%u expected=%ux%u",
                          label, d.Width, d.Height, width, height);
            error = buf;
            return false;
        }
        if (d.SampleDesc.Count != 1)
        {
            char buf[320];
            std::snprintf(buf, sizeof(buf), "%s MSAA unsupported: samples=%u",
                          label, d.SampleDesc.Count);
            error = buf;
            return false;
        }
        if (d.MipLevels != 1 || d.ArraySize != 1)
        {
            char buf[320];
            std::snprintf(buf, sizeof(buf), "%s unsupported layout: mipLevels=%u arraySize=%u",
                          label, d.MipLevels, d.ArraySize);
            error = buf;
            return false;
        }

        const bool formatOK = inputTexture ? IsR8CopyFamily(d.Format) : IsShort2CopyFamily(d.Format);
        if (!formatOK)
        {
            char buf[384];
            if (inputTexture)
            {
                std::snprintf(
                    buf, sizeof(buf),
                    "%s copy-incompatible DXGI=%u; expected R8_TYPELESS(60) or R8_UNORM(61)",
                    label, static_cast<unsigned>(d.Format));
            }
            else
            {
                std::snprintf(
                    buf, sizeof(buf),
                    "%s copy-incompatible DXGI=%u; expected R16G16_TYPELESS(33) or R16G16_SINT(38)",
                    label, static_cast<unsigned>(d.Format));
            }
            error = buf;
            return false;
        }

        return true;
    }

    NvOFBufferD3D11* AsD3D11Buffer(NvOFBufferObj& buffer)
    {
        return buffer ? dynamic_cast<NvOFBufferD3D11*>(buffer.get()) : nullptr;
    }


    bool EnsureSession(EyeState& s)
    {
        if (s.session)
            return true;
        if (!s.configured || !s.input[0] || !s.input[1] || !s.output)
        {
            SetError(s, -100, "eye resources are not configured");
            return false;
        }

        try
        {
            ComPtr<ID3D11Device> device0;
            ComPtr<ID3D11Device> device1;
            ComPtr<ID3D11Device> deviceOut;
            s.input[0]->GetDevice(device0.GetAddressOf());
            s.input[1]->GetDevice(device1.GetAddressOf());
            s.output->GetDevice(deviceOut.GetAddressOf());
            if (!device0 || device0.Get() != device1.Get() || device0.Get() != deviceOut.Get())
            {
                SetError(s, -101, "Unity NVOFA copy endpoints do not belong to one D3D11 device");
                return false;
            }

            ComPtr<ID3D11DeviceContext> context;
            device0->GetImmediateContext(context.GetAddressOf());
            if (!context)
            {
                SetError(s, -102, "D3D11 immediate context is null");
                return false;
            }

            s.device = device0;
            s.context = context;

            s.session = NvOFD3D11::Create(
                s.device.Get(), s.context.Get(), s.width, s.height,
                NV_OF_BUFFER_FORMAT_GRAYSCALE8,
                NV_OF_MODE_OPTICALFLOW,
                NV_OF_PERF_LEVEL_MEDIUM);

            if (!s.session)
            {
                SetError(s, -103, "NvOFD3D11::Create returned null");
                return false;
            }

            if (!s.session->CheckGridSize(1))
            {
                s.ResetSessionOnly();
                SetError(s, -104, "NVOFA 1x1 output grid is not supported on this GPU/driver");
                return false;
            }

            s.session->Init(1);

            // IMPORTANT V90d FIX:
            // Do NOT register Unity's RenderTextures directly. Unity exposes our
            // GraphicsFormat.R8_UNorm RT as DXGI_FORMAT_R8_TYPELESS on this game.
            // NVOFA rejects that resource even though Unity binds a typed UNORM view.
            //
            // Instead ask NVIDIA's wrapper to allocate correctly typed resources:
            //   input  = DXGI_FORMAT_R8_UNORM
            //   output = DXGI_FORMAT_R16G16_SINT
            // and CopyResource between the compatible typeless/typed families.
            auto inputBuffers = s.session->CreateBuffers(NV_OF_BUFFER_USAGE_INPUT, 2);
            auto outputBuffers = s.session->CreateBuffers(NV_OF_BUFFER_USAGE_OUTPUT, 1);
            if (inputBuffers.size() != 2 || outputBuffers.size() != 1)
            {
                s.ResetSessionOnly();
                SetError(s, -105, "NVOFA typed proxy allocation returned unexpected buffer count");
                return false;
            }

            s.inputBuffer[0] = std::move(inputBuffers[0]);
            s.inputBuffer[1] = std::move(inputBuffers[1]);
            s.outputBuffer = std::move(outputBuffers[0]);

            auto* in0 = AsD3D11Buffer(s.inputBuffer[0]);
            auto* in1 = AsD3D11Buffer(s.inputBuffer[1]);
            auto* out = AsD3D11Buffer(s.outputBuffer);
            if (!in0 || !in1 || !out ||
                !in0->getD3D11TextureHandle() ||
                !in1->getD3D11TextureHandle() ||
                !out->getD3D11TextureHandle())
            {
                s.ResetSessionOnly();
                SetError(s, -106, "NVOFA typed proxy buffers are not D3D11 texture buffers");
                return false;
            }

            D3D11_TEXTURE2D_DESC d{};
            in0->getD3D11TextureHandle()->GetDesc(&d);
            s.proxyInputFormat[0] = d.Format;
            in1->getD3D11TextureHandle()->GetDesc(&d);
            s.proxyInputFormat[1] = d.Format;
            out->getD3D11TextureHandle()->GetDesc(&d);
            s.proxyOutputFormat = d.Format;

            if (s.proxyInputFormat[0] != DXGI_FORMAT_R8_UNORM ||
                s.proxyInputFormat[1] != DXGI_FORMAT_R8_UNORM ||
                s.proxyOutputFormat != DXGI_FORMAT_R16G16_SINT)
            {
                char buf[384];
                std::snprintf(
                    buf, sizeof(buf),
                    "NVOFA proxy format surprise: inputs=%u/%u expected=61/61 output=%u expected=38",
                    static_cast<unsigned>(s.proxyInputFormat[0]),
                    static_cast<unsigned>(s.proxyInputFormat[1]),
                    static_cast<unsigned>(s.proxyOutputFormat));
                s.ResetSessionOnly();
                SetError(s, -107, buf);
                return false;
            }

            s.stateCode = 2;
            s.lastErrorCode = 0;
            s.lastError.clear();
            return true;
        }
        catch (const NvOFException& e)
        {
            const int code = static_cast<int>(e.getErrorCode());
            const std::string message = std::string("NvOFException: ") + e.what();
            s.ResetSessionOnly();
            SetError(s, code, message);
            return false;
        }
        catch (const std::exception& e)
        {
            const std::string message = std::string("std::exception: ") + e.what();
            s.ResetSessionOnly();
            SetError(s, -108, message);
            return false;
        }
        catch (...)
        {
            s.ResetSessionOnly();
            SetError(s, -109, "unknown exception during NVOFA initialization");
            return false;
        }
    }


    void ExecuteEyeLocked(int eyeIndex)
    {
        EyeState& s = g_eye[eyeIndex];
        const int currentSlot = s.pendingCurrentSlot;
        const int frame = s.pendingFrame;
        const uint64_t serial = s.pendingSerial;

        if (currentSlot < 0 || currentSlot > 1 || frame < 0)
            return;
        if (frame == s.completedFrame)
            return;
        if (!EnsureSession(s))
            return;

        try
        {
            const int previousSlot = 1 - currentSlot;

            auto* currentProxy = AsD3D11Buffer(s.inputBuffer[currentSlot]);
            auto* previousProxy = AsD3D11Buffer(s.inputBuffer[previousSlot]);
            auto* outputProxy = AsD3D11Buffer(s.outputBuffer);
            if (!s.context || !currentProxy || !previousProxy || !outputProxy)
            {
                SetError(s, -120, "typed D3D11 proxy state disappeared before execute");
                return;
            }

            // GPU-only bridge from Unity's TYPELESS-compatible resources into
            // NVOFA's typed resources. D3D11 permits CopyResource within a format
            // family (R8_TYPELESS <-> R8_UNORM and R16G16_TYPELESS <-> SINT).
            s.context->CopyResource(
                currentProxy->getD3D11TextureHandle(),
                s.input[currentSlot].Get());
            s.context->CopyResource(
                previousProxy->getD3D11TextureHandle(),
                s.input[previousSlot].Get());

            const auto t0 = std::chrono::high_resolution_clock::now();

            // CURRENT -> PREVIOUS on purpose.  The resulting vector is defined at
            // current pixels, exactly where V90's stale source color/depth live.
            s.session->Execute(
                s.inputBuffer[currentSlot].get(),
                s.inputBuffer[previousSlot].get(),
                s.outputBuffer.get(),
                nullptr,
                nullptr,
                0,
                nullptr,
                nullptr,
                0,
                nullptr,
                NV_OF_TRUE); // no temporal hints: alternating per-eye cadence is explicit

            const auto t1 = std::chrono::high_resolution_clock::now();
            s.lastApiCallMs = std::chrono::duration<double, std::milli>(t1 - t0).count();

            // Keep Unity's existing flow RT as the shader-facing texture. Copy the
            // typed NVOFA result back into Unity's compatible (often TYPELESS)
            // resource without CPU readback or a shader conversion pass.
            s.context->CopyResource(
                s.output.Get(),
                outputProxy->getD3D11TextureHandle());
            s.completedFrame = frame;
            s.completedCurrentSlot = currentSlot;
            s.executeCount++;
            s.stateCode = 3;
            s.lastErrorCode = 0;
            s.lastError.clear();

            // If another request arrived while the render-thread event was executing,
            // leave it for the next plugin event rather than lying about completion.
            (void)serial;
        }
        catch (const NvOFException& e)
        {
            SetError(s, static_cast<int>(e.getErrorCode()), std::string("NvOF execute: ") + e.what());
        }
        catch (const std::exception& e)
        {
            SetError(s, -121, std::string("execute exception: ") + e.what());
        }
        catch (...)
        {
            SetError(s, -122, "unknown exception during NVOFA execute");
        }
    }

    void __stdcall OnRenderEvent(int eventId)
    {
        const int eyeIndex = eventId - kEventBase;
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return;

        std::lock_guard<std::mutex> lock(g_mutex);
        ExecuteEyeLocked(eyeIndex);
    }
}

extern "C"
{
    __declspec(dllexport) void* __cdecl NvOFV90_GetRenderEventFunc()
    {
        return reinterpret_cast<void*>(&OnRenderEvent);
    }

    __declspec(dllexport) int __cdecl NvOFV90_GetEventBase()
    {
        return kEventBase;
    }

    __declspec(dllexport) int __cdecl NvOFV90_ConfigureEye(
        int eyeIndex,
        void* input0,
        void* input1,
        void* output,
        int width,
        int height)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount || !input0 || !input1 || !output || width <= 0 || height <= 0)
            return 0;

        std::lock_guard<std::mutex> lock(g_mutex);
        EyeState& s = g_eye[eyeIndex];

        ID3D11Texture2D* in0 = reinterpret_cast<ID3D11Texture2D*>(input0);
        ID3D11Texture2D* in1 = reinterpret_cast<ID3D11Texture2D*>(input1);
        ID3D11Texture2D* out = reinterpret_cast<ID3D11Texture2D*>(output);

        std::string error;
        DXGI_FORMAT in0Format = DXGI_FORMAT_UNKNOWN;
        DXGI_FORMAT in1Format = DXGI_FORMAT_UNKNOWN;
        DXGI_FORMAT outFormat = DXGI_FORMAT_UNKNOWN;
        if (!ValidateUnityTexture(
                in0, static_cast<uint32_t>(width), static_cast<uint32_t>(height),
                true, "input0", in0Format, error) ||
            !ValidateUnityTexture(
                in1, static_cast<uint32_t>(width), static_cast<uint32_t>(height),
                true, "input1", in1Format, error) ||
            !ValidateUnityTexture(
                out, static_cast<uint32_t>(width), static_cast<uint32_t>(height),
                false, "output", outFormat, error))
        {
            SetError(s, -110, error);
            return 0;
        }

        s.ResetSessionOnly();
        s.input[0] = in0;
        s.input[1] = in1;
        s.output = out;
        s.unityInputFormat[0] = in0Format;
        s.unityInputFormat[1] = in1Format;
        s.unityOutputFormat = outFormat;
        s.width = static_cast<uint32_t>(width);
        s.height = static_cast<uint32_t>(height);
        s.configured = true;
        s.pendingCurrentSlot = -1;
        s.pendingFrame = -1;
        s.stateCode = 1;
        s.lastErrorCode = 0;
        s.lastError.clear();
        return 1;
    }

    __declspec(dllexport) int __cdecl NvOFV90_RequestEye(int eyeIndex, int currentSlot, int frame)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount || currentSlot < 0 || currentSlot > 1 || frame < 0)
            return 0;

        std::lock_guard<std::mutex> lock(g_mutex);
        EyeState& s = g_eye[eyeIndex];
        if (!s.configured)
            return 0;
        s.pendingCurrentSlot = currentSlot;
        s.pendingFrame = frame;
        s.pendingSerial++;
        return 1;
    }

    // which:
    //   0/1 = Unity input0/input1 resource DXGI
    //   2   = Unity output resource DXGI
    //   3/4 = NVOFA typed input0/input1 proxy DXGI
    //   5   = NVOFA typed output proxy DXGI
    __declspec(dllexport) int __cdecl NvOFV90_GetDxgiFormat(int eyeIndex, int which)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return -1;
        std::lock_guard<std::mutex> lock(g_mutex);
        const EyeState& s = g_eye[eyeIndex];
        switch (which)
        {
        case 0: return static_cast<int>(s.unityInputFormat[0]);
        case 1: return static_cast<int>(s.unityInputFormat[1]);
        case 2: return static_cast<int>(s.unityOutputFormat);
        case 3: return static_cast<int>(s.proxyInputFormat[0]);
        case 4: return static_cast<int>(s.proxyInputFormat[1]);
        case 5: return static_cast<int>(s.proxyOutputFormat);
        default: return -1;
        }
    }

    __declspec(dllexport) int __cdecl NvOFV90_GetState(int eyeIndex)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return -999;
        std::lock_guard<std::mutex> lock(g_mutex);
        return g_eye[eyeIndex].stateCode;
    }

    __declspec(dllexport) int __cdecl NvOFV90_GetCompletedFrame(int eyeIndex)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return -1;
        std::lock_guard<std::mutex> lock(g_mutex);
        return g_eye[eyeIndex].completedFrame;
    }

    __declspec(dllexport) unsigned long long __cdecl NvOFV90_GetExecuteCount(int eyeIndex)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return 0;
        std::lock_guard<std::mutex> lock(g_mutex);
        return g_eye[eyeIndex].executeCount;
    }

    __declspec(dllexport) double __cdecl NvOFV90_GetLastApiCallMs(int eyeIndex)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount)
            return -1.0;
        std::lock_guard<std::mutex> lock(g_mutex);
        return g_eye[eyeIndex].lastApiCallMs;
    }

    __declspec(dllexport) int __cdecl NvOFV90_GetLastError(int eyeIndex, char* buffer, int capacity)
    {
        if (eyeIndex < 0 || eyeIndex >= kEyeCount || !buffer || capacity <= 0)
            return -999;
        std::lock_guard<std::mutex> lock(g_mutex);
        const EyeState& s = g_eye[eyeIndex];
        std::snprintf(buffer, static_cast<size_t>(capacity), "%s", s.lastError.c_str());
        return s.lastErrorCode;
    }

    __declspec(dllexport) unsigned int __cdecl NvOFV90_GetMaxSupportedApiVersion()
    {
        HMODULE module = LoadLibraryW(L"nvofapi64.dll");
        if (!module)
            return 0;
        using Fn = NV_OF_STATUS (NVOFAPI*)(uint32_t*);
        auto fn = reinterpret_cast<Fn>(GetProcAddress(module, "NvOFGetMaxSupportedApiVersion"));
        uint32_t version = 0;
        if (fn)
            fn(&version);
        FreeLibrary(module);
        return version;
    }

    __declspec(dllexport) void __cdecl NvOFV90_Reset()
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (auto& eye : g_eye)
            eye.ResetAll();
    }
}
