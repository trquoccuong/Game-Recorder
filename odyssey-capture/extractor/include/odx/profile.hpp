// Per-game Lua profile host.
//
// A profile is a Lua file returning a table:
//   game, engine            strings
//   convention              "rfu" | "unity" | "unreal" | "quake"
//   units_to_world          number (default 1.0), e.g. 0.01 for Unreal centimetres
//   fov_axis                "vertical" | "horizontal"
//   camera_model            default "PINHOLE"
//   mouse_convention        { maps_to, dx_positive, dx_negative, dy_positive, dy_negative }
//   bindings                { [key or mouse button name] = action name }
//   init()                  optional; called once to resolve signatures
//   sample()                returns { position, forward, right, up, fov } (vectors as {x, y, z} in engine axes)
//
// Global `mem` API available to profiles (all addresses are Lua integers):
//   mem.module(name) -> base, size | nil      mem.scan(module, "48 8B 05 ?? ...") -> addr | nil
//   mem.rip(addr, disp_offset, instr_len) -> addr | nil
//   mem.read_ptr / read_i32 / read_u32 / read_f32 / read_f64 (addr) -> value   (raises on unreadable memory)
//   mem.read_vec3(addr) -> {x, y, z} from three float32
#pragma once

#include <map>
#include <memory>
#include <optional>
#include <string>

#include "odx/math.hpp"
#include "odx/memory.hpp"
#include "odx/output.hpp"

struct lua_State;

namespace odx {

struct ProfileInfo {
    std::string game, engine;
    Convention convention = Convention::RFU;
    double units_to_world = 1.0;
    bool fov_vertical = true;
    std::string camera_model = "PINHOLE";
    MouseConvention mouse;
    std::map<std::string, std::string> bindings;
};

struct CameraSample {
    Mat4 c2w;
    Intrinsics intrinsics;
};

class Profile {
public:
    static std::unique_ptr<Profile> load(const std::string& path, MemoryReader& mem, std::string& error);
    ~Profile();
    Profile(const Profile&) = delete;
    Profile& operator=(const Profile&) = delete;

    const ProfileInfo& info() const { return info_; }
    // Runs init() if present; returns false and sets last_error() on failure (retry later, e.g. game still loading).
    bool init();
    // Calls sample() and converts to canonical C2W + intrinsics for the output resolution.
    std::optional<CameraSample> sample(int width, int height);
    const std::string& last_error() const { return last_error_; }

private:
    Profile() = default;
    lua_State* L_ = nullptr;
    int table_ref_ = -1;
    ProfileInfo info_;
    std::string last_error_;
};

}  // namespace odx
