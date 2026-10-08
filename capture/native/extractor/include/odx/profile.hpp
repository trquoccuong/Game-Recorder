// Per-game Lua profile host.
//
// A profile is a Lua file returning a table:
//   game, engine            strings
//   convention              "rfu" | "unity" | "unreal" | "quake"   (engine world axes)
//   units_to_world          number (default 1.0), e.g. 0.01 for Unreal centimetres
//   fov_axis                "vertical" | "horizontal"
//   camera_model            default "PINHOLE"
//   init()                  optional; called once to resolve signatures
//   sample()                returns { position, forward, right, up, fov } (vectors as {x, y, z} in engine axes)
// Samples stay in engine axes and units; `odx build` (dotnet/) converts them to the canonical C2W using the axis
// fields above. Key bindings and session.json fields live in the game profile JSON (capture/profiles/).
//
// Global `mem` API available to profiles (all addresses are Lua integers):
//   mem.module(name) -> base, size | nil      mem.scan(module, "48 8B 05 ?? ...") -> addr | nil
//   mem.rip(addr, disp_offset, instr_len) -> addr | nil
//   mem.read_ptr / read_i32 / read_u32 / read_f32 / read_f64 (addr) -> value   (raises on unreadable memory)
//   mem.read_vec3(addr) -> {x, y, z} from three float32
#pragma once

#include <memory>
#include <optional>
#include <string>

#include "odx/memory.hpp"

struct lua_State;

namespace odx {

struct Vec3 {
    double x = 0, y = 0, z = 0;
};

struct ProfileInfo {
    std::string game, engine;
    std::string convention = "rfu";
    double units_to_world = 1.0;
    std::string fov_axis = "vertical";
    std::string camera_model = "PINHOLE";
};

// One sample() result, in engine axes and units.
struct CameraSample {
    Vec3 position, forward, right, up;
    double fov_deg = 0;
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
    std::optional<CameraSample> sample();
    const std::string& last_error() const { return last_error_; }

private:
    Profile() = default;
    lua_State* L_ = nullptr;
    int table_ref_ = -1;
    ProfileInfo info_;
    std::string last_error_;
};

}  // namespace odx
