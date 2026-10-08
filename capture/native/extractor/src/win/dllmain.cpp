// odx_extractor.dll — loaded into the game process.
//
// On load: reads odx_config.lua next to the DLL, loads the game profile and hooks Present. The toggle key (default F9)
// starts/stops a recording; each one writes <out_dir>/<game>-<UTC time>/camera_samples.csv + extractor.json, to be
// merged by `odx build --camera <that folder>` with an odx input + odx record capture of the same play.
// Log: odx_log.txt next to the DLL.
#include <windows.h>

#include <cctype>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#include "common.hpp"
#include "odx/config.hpp"
#include "odx/profile.hpp"
#include "odx/recorder.hpp"
#include "present_hook.hpp"
#include "process_memory.hpp"

namespace {

using namespace odx;

std::filesystem::path g_dll_dir;
Config g_config;
win::ProcessMemory g_memory;
std::unique_ptr<Profile> g_profile;
bool g_profile_ready = false;
double g_last_init_attempt = 0;
int g_toggle_vk = 0;
bool g_toggle_down = false;
std::unique_ptr<Recorder> g_recorder;  // Present thread only

void log(const std::string& msg) {
    static std::mutex mu;
    std::lock_guard lock(mu);
    if (FILE* f = _wfopen((g_dll_dir / "odx_log.txt").c_str(), L"a")) {
        std::fprintf(f, "[%.3f] %s\n", win::qpc_ms() / 1000.0, msg.c_str());
        std::fclose(f);
    }
}

std::string timestamp_id() {
    std::time_t now = std::time(nullptr);
    std::tm tm{};
    gmtime_s(&tm, &now);
    char buf[32];
    std::strftime(buf, sizeof(buf), "%Y%m%dT%H%M%SZ", &tm);
    return buf;
}

std::string sanitize(std::string s) {
    for (char& c : s)
        if (!std::isalnum(static_cast<unsigned char>(c))) c = '-';
    return s;
}

// "F1".."F24", "A".."Z", "0".."9" (odx input key names); 0 if unsupported.
int virtual_key(const std::string& name) {
    if (name.size() == 1 && std::isalnum(static_cast<unsigned char>(name[0])))
        return std::toupper(static_cast<unsigned char>(name[0]));
    if (name.size() >= 2 && name[0] == 'F') {
        int n = std::atoi(name.c_str() + 1);
        if (n >= 1 && n <= 24) return VK_F1 + n - 1;
    }
    return 0;
}

void toggle_recording() {
    if (g_recorder) {
        int64_t n = g_recorder->stop();
        log("recording stopped: " + std::to_string(n) + " camera samples");
        g_recorder.reset();
        return;
    }
    RecorderConfig cfg;
    cfg.dir = (std::filesystem::path(g_config.out_dir) / (sanitize(g_profile->info().game) + "-" + timestamp_id())).string();
    cfg.profile = g_profile->info();
    auto rec = std::make_unique<Recorder>(cfg);
    std::string error;
    if (!rec->start(error)) {
        log("cannot start recording: " + error);
        return;
    }
    g_recorder = std::move(rec);
    log("recording started: " + cfg.dir);
}

// Polling the async key state reads input without registering for it, so the game keeps all of its own input.
void on_present(IDXGISwapChain*) {
    const double t = win::qpc_ms();
    bool down = (GetAsyncKeyState(g_toggle_vk) & 0x8000) != 0;
    if (down && !g_toggle_down) toggle_recording();
    g_toggle_down = down;

    if (!g_profile_ready && t - g_last_init_attempt > 1000) {  // game may still be loading: retry each second
        g_last_init_attempt = t;
        g_profile_ready = g_profile->init();
        log(g_profile_ready ? "profile init ok" : "profile init failed: " + g_profile->last_error());
    }
    if (!g_recorder || !g_profile_ready) return;
    if (auto sample = g_profile->sample()) {
        g_recorder->on_frame(t, *sample);
    } else {
        log("sample failed: " + g_profile->last_error());
    }
}

void init(HMODULE self) {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(self, path, MAX_PATH);
    g_dll_dir = std::filesystem::path(path).parent_path();
    log("odx_extractor loaded, pid " + std::to_string(GetCurrentProcessId()));

    std::string error;
    auto cfg = load_config((g_dll_dir / "odx_config.lua").string(), error);
    if (!cfg) return log("config error: " + error);
    g_config = *cfg;
    g_toggle_vk = virtual_key(g_config.toggle_key);
    if (!g_toggle_vk) return log("config error: unsupported toggle_key " + g_config.toggle_key);
    g_profile = Profile::load(g_config.profile, g_memory, error);
    if (!g_profile) return log("profile error: " + error);
    log("profile loaded: " + g_profile->info().game);

    if (!win::install_present_hook(on_present, error)) return log("present hook error: " + error);
    log("present hook installed; press " + g_config.toggle_key + " to start/stop recording");
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // Loader lock is held here: do the real work on a separate thread.
        std::thread([module] { init(module); }).detach();
    }
    return TRUE;
}
