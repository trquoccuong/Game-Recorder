// Shared Windows helpers: QPC clock and the input IPC message sent from odx-input (capture-cs, C#) to the DLL.
// The C# encoder is Odx.Core.InputMsg; keep both layouts identical.
//
// Input is captured in a separate process because Raw Input allows one registered window per device class
// per process; registering inside the game would steal the game's own raw input. QPC is system-wide, so
// timestamps from both processes share one clock.
#pragma once

#include <windows.h>

#include <cstdint>
#include <string>

namespace odx::win {

inline double qpc_ms() {
    static const double freq = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return static_cast<double>(f.QuadPart);
    }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return static_cast<double>(c.QuadPart) * 1000.0 / freq;
}

enum class MsgKind : uint8_t { KeyDown, KeyUp, ButtonDown, ButtonUp, MouseMove };

#pragma pack(push, 1)
struct InputMsg {
    MsgKind kind;
    double t_ms;  // qpc_ms() at receipt
    float dx, dy;
    char value[32];  // key / button name, NUL-terminated
};
#pragma pack(pop)

inline std::wstring mailslot_name(DWORD pid) { return L"\\\\.\\mailslot\\odx_input_" + std::to_wstring(pid); }

}  // namespace odx::win
