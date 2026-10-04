#include "odx/config.hpp"

#include <filesystem>
#include <lua.hpp>

namespace odx {

namespace {
std::string get_string(lua_State* L, const char* key, const std::string& fallback) {
    lua_getfield(L, -1, key);
    std::string v = lua_isstring(L, -1) ? lua_tostring(L, -1) : fallback;
    lua_pop(L, 1);
    return v;
}
}  // namespace

std::optional<Config> load_config(const std::string& path, std::string& error) {
    lua_State* L = luaL_newstate();
    luaL_openlibs(L);
    std::optional<Config> out;
    if (luaL_dofile(L, path.c_str()) != LUA_OK) {
        error = lua_tostring(L, -1);
    } else if (!lua_istable(L, -1)) {
        error = path + ": config must return a table";
    } else {
        Config c;
        auto dir = std::filesystem::path(path).parent_path();
        auto resolve = [&](const std::string& p) {
            return p.empty() || std::filesystem::path(p).is_absolute() ? p : (dir / p).string();
        };
        c.profile = resolve(get_string(L, "profile", ""));
        c.out_dir = resolve(get_string(L, "out_dir", "odx_capture"));
        c.vendor_name = get_string(L, "vendor_name", c.vendor_name);
        c.toggle_key = get_string(L, "toggle_key", c.toggle_key);
        lua_getfield(L, -1, "fps");
        if (lua_isnumber(L, -1)) c.fps = lua_tonumber(L, -1);
        lua_pop(L, 1);
        if (c.profile.empty()) {
            error = path + ": 'profile' is required";
        } else {
            out = c;
        }
    }
    lua_close(L);
    return out;
}

}  // namespace odx
