// A fake game process for tests and odx_synth: a "game.exe" module whose code holds
//   48 8B 05 <disp32>  48 85 C0 74 05      (mov rax,[rip+disp]; test rax,rax; jz +5)
// where disp points at a global pointer to a camera struct on a "heap" region:
//   float position[3], forward[3], right[3], up[3], fov      (Unreal axes, centimetres, horizontal FOV)
// profiles/synthetic.lua finds the struct by signature, like a real profile would.
#pragma once

#include "odx/math.hpp"
#include "odx/memory.hpp"

namespace odx {

class FakeGame {
public:
    FakeGame();
    MemoryReader& memory() { return mem_; }
    // Writes a camera pose given in canonical RFU (metres) into the struct, converted to Unreal axes (cm).
    void set_camera(const Mat4& c2w_rfu, double hfov_deg);

private:
    BufferMemory mem_;
};

}  // namespace odx
