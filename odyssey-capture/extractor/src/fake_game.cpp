#include "odx/fake_game.hpp"

#include <cstring>

namespace odx {

namespace {
constexpr uint64_t kModuleBase = 0x140000000;
constexpr uint64_t kCodeOffset = 0x100;
constexpr uint64_t kGlobalOffset = 0x1000;
constexpr uint64_t kHeapBase = 0x20000000;
constexpr uint64_t kCameraOffset = 0x40;

// Unreal <-> RFU is a swap of X and Y (its own inverse).
Vec3 rfu_to_unreal(Vec3 v) { return {v.y, v.x, v.z}; }

void put_vec3(std::vector<uint8_t>& b, size_t at, Vec3 v) {
    float f[3] = {static_cast<float>(v.x), static_cast<float>(v.y), static_cast<float>(v.z)};
    std::memcpy(b.data() + at, f, sizeof(f));
}
}  // namespace

FakeGame::FakeGame() {
    std::vector<uint8_t> module(0x2000, 0xCC);
    const uint64_t instr = kModuleBase + kCodeOffset;
    const int32_t disp = static_cast<int32_t>((kModuleBase + kGlobalOffset) - (instr + 7));
    uint8_t code[] = {0x48, 0x8B, 0x05, 0, 0, 0, 0, 0x48, 0x85, 0xC0, 0x74, 0x05};
    std::memcpy(code + 3, &disp, sizeof(disp));
    std::memcpy(module.data() + kCodeOffset, code, sizeof(code));
    const uint64_t camera_addr = kHeapBase + kCameraOffset;
    std::memcpy(module.data() + kGlobalOffset, &camera_addr, sizeof(camera_addr));
    mem_.add_module("game.exe", kModuleBase, std::move(module));
    mem_.add_module("heap", kHeapBase, std::vector<uint8_t>(0x100, 0));
}

void FakeGame::set_camera(const Mat4& m, double hfov_deg) {
    auto col = [&](int c) { return Vec3{m[0][c], m[1][c], m[2][c]}; };
    Vec3 right = col(0), up = col(1), back = col(2), pos = col(3);
    Vec3 fwd{-back.x, -back.y, -back.z};
    Vec3 pos_cm = rfu_to_unreal(pos);
    pos_cm = {pos_cm.x * 100, pos_cm.y * 100, pos_cm.z * 100};
    auto& heap = mem_.bytes("heap");
    put_vec3(heap, kCameraOffset + 0, pos_cm);
    put_vec3(heap, kCameraOffset + 12, rfu_to_unreal(fwd));
    put_vec3(heap, kCameraOffset + 24, rfu_to_unreal(right));
    put_vec3(heap, kCameraOffset + 36, rfu_to_unreal(up));
    float fov = static_cast<float>(hfov_deg);
    std::memcpy(heap.data() + kCameraOffset + 48, &fov, sizeof(fov));
}

}  // namespace odx
