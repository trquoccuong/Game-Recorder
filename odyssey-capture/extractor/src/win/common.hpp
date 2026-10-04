// QPC clock shared with odx-input and odx-record (capture-cs): QueryPerformanceCounter is system-wide, so camera
// samples taken in the game process line up with input and video captured by those separate processes.
#pragma once

#include <windows.h>

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

}  // namespace odx::win
