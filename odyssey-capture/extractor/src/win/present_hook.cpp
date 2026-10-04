#include "present_hook.hpp"

#include <windows.h>
#include <d3d11.h>
#include <MinHook.h>

namespace odx::win {

namespace {

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);

PresentFn g_original = nullptr;
void* g_target = nullptr;
PresentCallback g_callback;

HRESULT STDMETHODCALLTYPE hooked_present(IDXGISwapChain* swapchain, UINT sync_interval, UINT flags) {
    if (g_callback) {
        try {
            g_callback(swapchain);
        } catch (...) {
            // Never let capture errors propagate into the game's render thread.
        }
    }
    return g_original(swapchain, sync_interval, flags);
}

// Creates a throwaway device + swapchain to read the Present address from its vtable.
void* find_present_address(std::string& error) {
    WNDCLASSEXW wc{sizeof(wc)};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"odx_dummy_window";
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 64, 64, nullptr, nullptr,
                                wc.hInstance, nullptr);

    DXGI_SWAP_CHAIN_DESC sd{};
    sd.BufferCount = 1;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    void* present = nullptr;
    for (D3D_DRIVER_TYPE type : {D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP}) {
        HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, type, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &sd, &sc,
                                                   &dev, nullptr, &ctx);
        if (SUCCEEDED(hr)) {
            present = (*reinterpret_cast<void***>(sc))[8];
            break;
        }
        error = "D3D11CreateDeviceAndSwapChain failed: 0x" + std::to_string(static_cast<unsigned long>(hr));
    }
    if (sc) sc->Release();
    if (ctx) ctx->Release();
    if (dev) dev->Release();
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return present;
}

}  // namespace

bool install_present_hook(PresentCallback cb, std::string& error) {
    void* target = find_present_address(error);
    if (!target) return false;
    MH_STATUS st = MH_Initialize();
    if (st != MH_OK && st != MH_ERROR_ALREADY_INITIALIZED) {
        error = std::string("MH_Initialize: ") + MH_StatusToString(st);
        return false;
    }
    g_callback = std::move(cb);
    st = MH_CreateHook(target, reinterpret_cast<void*>(&hooked_present), reinterpret_cast<void**>(&g_original));
    if (st != MH_OK) {
        error = std::string("MH_CreateHook: ") + MH_StatusToString(st);
        return false;
    }
    st = MH_EnableHook(target);
    if (st != MH_OK) {
        error = std::string("MH_EnableHook: ") + MH_StatusToString(st);
        return false;
    }
    g_target = target;
    return true;
}

void remove_present_hook() {
    if (g_target) {
        MH_DisableHook(g_target);
        MH_RemoveHook(g_target);
        g_target = nullptr;
    }
}

}  // namespace odx::win
