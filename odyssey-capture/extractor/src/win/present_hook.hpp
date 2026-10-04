// Hooks IDXGISwapChain::Present (vtable slot 8) so a callback runs once per presented frame, just before
// the frame is handed to DXGI. Covers D3D11 and D3D12 games presenting through IDXGISwapChain::Present.
// Not covered yet: games presenting via Present1 only, Vulkan, OpenGL.
#pragma once

#include <dxgi.h>

#include <functional>
#include <string>

namespace odx::win {

using PresentCallback = std::function<void(IDXGISwapChain*)>;

bool install_present_hook(PresentCallback cb, std::string& error);
void remove_present_hook();

}  // namespace odx::win
