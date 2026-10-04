#include "odx/profile.hpp"

#include <cstdio>
#include <lua.hpp>

namespace odx {

namespace {

MemoryReader& mem_of(lua_State* L) { return *static_cast<MemoryReader*>(lua_touserdata(L, lua_upvalueindex(1))); }

uint64_t check_addr(lua_State* L, int idx) { return static_cast<uint64_t>(luaL_checkinteger(L, idx)); }

template <typename T>
T read_or_raise(lua_State* L, uint64_t addr) {
    T v{};
    if (!mem_of(L).read(addr, &v, sizeof(v))) luaL_error(L, "unreadable memory at 0x%llx", (unsigned long long)addr);
    return v;
}

int l_module(lua_State* L) {
    auto span = mem_of(L).module(luaL_checkstring(L, 1));
    if (!span) return lua_pushnil(L), 1;
    lua_pushinteger(L, static_cast<lua_Integer>(span->base));
    lua_pushinteger(L, static_cast<lua_Integer>(span->size));
    return 2;
}

int l_scan(lua_State* L) {
    auto span = mem_of(L).module(luaL_checkstring(L, 1));
    if (!span) return luaL_error(L, "module not found: %s", lua_tostring(L, 1));
    auto pattern = parse_pattern(luaL_checkstring(L, 2));
    if (!pattern) return luaL_error(L, "invalid pattern: %s", lua_tostring(L, 2));
    auto hit = scan(mem_of(L), *span, *pattern);
    if (!hit) return lua_pushnil(L), 1;
    return lua_pushinteger(L, static_cast<lua_Integer>(*hit)), 1;
}

int l_rip(lua_State* L) {
    auto addr = resolve_rip(mem_of(L), check_addr(L, 1), static_cast<int>(luaL_checkinteger(L, 2)),
                            static_cast<int>(luaL_checkinteger(L, 3)));
    if (!addr) return lua_pushnil(L), 1;
    return lua_pushinteger(L, static_cast<lua_Integer>(*addr)), 1;
}

int l_read_ptr(lua_State* L) { return lua_pushinteger(L, static_cast<lua_Integer>(read_or_raise<uint64_t>(L, check_addr(L, 1)))), 1; }
int l_read_i32(lua_State* L) { return lua_pushinteger(L, read_or_raise<int32_t>(L, check_addr(L, 1))), 1; }
int l_read_u32(lua_State* L) { return lua_pushinteger(L, read_or_raise<uint32_t>(L, check_addr(L, 1))), 1; }
int l_read_f32(lua_State* L) { return lua_pushnumber(L, read_or_raise<float>(L, check_addr(L, 1))), 1; }
int l_read_f64(lua_State* L) { return lua_pushnumber(L, read_or_raise<double>(L, check_addr(L, 1))), 1; }

int l_read_vec3(lua_State* L) {
    uint64_t a = check_addr(L, 1);
    lua_createtable(L, 3, 0);
    for (int i = 0; i < 3; ++i) {
        lua_pushnumber(L, read_or_raise<float>(L, a + 4 * i));
        lua_rawseti(L, -2, i + 1);
    }
    return 1;
}

void register_mem(lua_State* L, MemoryReader& mem) {
    const luaL_Reg fns[] = {{"module", l_module},     {"scan", l_scan},         {"rip", l_rip},
                            {"read_ptr", l_read_ptr}, {"read_i32", l_read_i32}, {"read_u32", l_read_u32},
                            {"read_f32", l_read_f32}, {"read_f64", l_read_f64}, {"read_vec3", l_read_vec3},
                            {nullptr, nullptr}};
    lua_newtable(L);
    lua_pushlightuserdata(L, &mem);
    luaL_setfuncs(L, fns, 1);
    lua_setglobal(L, "mem");
}

std::string field_string(lua_State* L, int t, const char* key, const std::string& fallback) {
    lua_getfield(L, t, key);
    std::string v = lua_isstring(L, -1) ? lua_tostring(L, -1) : fallback;
    lua_pop(L, 1);
    return v;
}

bool field_vec3(lua_State* L, int t, const char* key, Vec3& out) {
    lua_getfield(L, t, key);
    bool ok = lua_istable(L, -1);
    double v[3] = {0, 0, 0};
    for (int i = 0; ok && i < 3; ++i) {
        lua_rawgeti(L, -1, i + 1);
        ok = lua_isnumber(L, -1);
        v[i] = lua_tonumber(L, -1);
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    out = {v[0], v[1], v[2]};
    return ok;
}

}  // namespace

std::unique_ptr<Profile> Profile::load(const std::string& path, MemoryReader& mem, std::string& error) {
    std::unique_ptr<Profile> p(new Profile());
    p->L_ = luaL_newstate();
    lua_State* L = p->L_;
    luaL_openlibs(L);
    register_mem(L, mem);

    if (luaL_dofile(L, path.c_str()) != LUA_OK) {
        error = lua_tostring(L, -1);
        return nullptr;
    }
    if (!lua_istable(L, -1)) {
        error = path + ": profile must return a table";
        return nullptr;
    }
    int t = lua_gettop(L);
    auto& info = p->info_;
    info.game = field_string(L, t, "game", "");
    info.engine = field_string(L, t, "engine", "");
    info.convention = field_string(L, t, "convention", info.convention);
    if (info.convention != "rfu" && info.convention != "unity" && info.convention != "unreal" &&
        info.convention != "quake") {
        error = path + ": convention must be 'rfu', 'unity', 'unreal' or 'quake'";
        return nullptr;
    }
    lua_getfield(L, t, "units_to_world");
    info.units_to_world = lua_isnumber(L, -1) ? lua_tonumber(L, -1) : 1.0;
    lua_pop(L, 1);
    info.fov_axis = field_string(L, t, "fov_axis", info.fov_axis);
    if (info.fov_axis != "vertical" && info.fov_axis != "horizontal") {
        error = path + ": fov_axis must be 'vertical' or 'horizontal'";
        return nullptr;
    }
    info.camera_model = field_string(L, t, "camera_model", info.camera_model);

    lua_getfield(L, t, "sample");
    bool has_sample = lua_isfunction(L, -1);
    lua_pop(L, 1);
    if (!has_sample) {
        error = path + ": profile must define sample()";
        return nullptr;
    }
    p->table_ref_ = luaL_ref(L, LUA_REGISTRYINDEX);
    return p;
}

Profile::~Profile() {
    if (L_) lua_close(L_);
}

bool Profile::init() {
    lua_rawgeti(L_, LUA_REGISTRYINDEX, table_ref_);
    lua_getfield(L_, -1, "init");
    if (!lua_isfunction(L_, -1)) {
        lua_pop(L_, 2);
        return true;
    }
    bool ok = lua_pcall(L_, 0, 0, 0) == LUA_OK;
    if (!ok) {
        last_error_ = lua_tostring(L_, -1);
        lua_pop(L_, 1);
    }
    lua_pop(L_, 1);
    return ok;
}

std::optional<CameraSample> Profile::sample() {
    lua_rawgeti(L_, LUA_REGISTRYINDEX, table_ref_);
    lua_getfield(L_, -1, "sample");
    if (lua_pcall(L_, 0, 1, 0) != LUA_OK) {
        last_error_ = lua_tostring(L_, -1);
        lua_pop(L_, 2);
        return std::nullopt;
    }
    std::optional<CameraSample> out;
    int r = lua_gettop(L_);
    CameraSample s;
    lua_getfield(L_, r, "fov");
    bool has_fov = lua_isnumber(L_, -1);
    s.fov_deg = lua_tonumber(L_, -1);
    lua_pop(L_, 1);
    if (lua_istable(L_, r) && has_fov && field_vec3(L_, r, "position", s.position) &&
        field_vec3(L_, r, "forward", s.forward) && field_vec3(L_, r, "right", s.right) && field_vec3(L_, r, "up", s.up)) {
        out = s;
    } else {
        last_error_ = "sample() must return {position, forward, right, up, fov}";
    }
    lua_pop(L_, 2);
    return out;
}

}  // namespace odx
