// client_extractor.dll — loaded into the game process.
//
// On load: reads odx_config.lua next to the DLL, loads the game profile, hooks Present and listens for input
// from odx-input (capture-cs, run with --pid <game pid> --forward). Pressing the toggle key (default F9) starts/stops a recording. Each recording writes
// <out_dir>/<session_id>/frames.csv + session.json. Log: odx_log.txt next to the DLL.
#include <windows.h>

#include <atomic>
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

std::mutex g_rec_mu;  // guards g_recorder (Present thread swaps it, input thread feeds it)
std::unique_ptr<Recorder> g_recorder;
std::atomic<bool> g_toggle_requested = false;

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

// Runs on the Present thread only.
void toggle_recording() {
    std::lock_guard lock(g_rec_mu);
    if (g_recorder) {
        int64_t n = g_recorder->stop();
        log("recording stopped: " + std::to_string(n) + " frames");
        g_recorder.reset();
        return;
    }
    RecorderConfig cfg;
    cfg.session_id = sanitize(g_profile->info().game) + "-" + timestamp_id();
    cfg.session_dir = (std::filesystem::path(g_config.out_dir) / cfg.session_id).string();
    cfg.vendor_name = g_config.vendor_name;
    cfg.fps = g_config.fps;
    cfg.screen_width_px = GetSystemMetrics(SM_CXSCREEN);
    cfg.screen_height_px = GetSystemMetrics(SM_CYSCREEN);
    cfg.profile = g_profile->info();
    auto rec = std::make_unique<Recorder>(cfg);
    std::string error;
    if (!rec->start(error)) {
        log("cannot start recording: " + error);
        return;
    }
    g_recorder = std::move(rec);
    log("recording started: " + cfg.session_dir);
}

void on_present(IDXGISwapChain* swapchain) {
    const double t = win::qpc_ms();
    if (g_toggle_requested.exchange(false)) toggle_recording();

    if (!g_profile_ready && t - g_last_init_attempt > 1000) {  // game may still be loading: retry each second
        g_last_init_attempt = t;
        g_profile_ready = g_profile->init();
        log(g_profile_ready ? "profile init ok" : "profile init failed: " + g_profile->last_error());
    }

    std::lock_guard lock(g_rec_mu);
    if (!g_recorder) return;
    DXGI_SWAP_CHAIN_DESC desc{};
    swapchain->GetDesc(&desc);
    const int w = static_cast<int>(desc.BufferDesc.Width), h = static_cast<int>(desc.BufferDesc.Height);
    std::optional<CameraSample> cam;
    if (g_profile_ready) {
        cam = g_profile->sample(w, h);
        if (!cam) log("sample failed: " + g_profile->last_error());
    }
    g_recorder->on_frame(t, w, h, cam);
}

// Receives InputMsg datagrams from odx-input --forward.
void input_listener() {
    HANDLE slot = CreateMailslotW(win::mailslot_name(GetCurrentProcessId()).c_str(), sizeof(win::InputMsg),
                                  MAILSLOT_WAIT_FOREVER, nullptr);
    if (slot == INVALID_HANDLE_VALUE) {
        log("CreateMailslot failed: " + std::to_string(GetLastError()));
        return;
    }
    win::InputMsg msg{};
    DWORD read = 0;
    while (ReadFile(slot, &msg, sizeof(msg), &read, nullptr)) {
        if (read != sizeof(msg)) continue;
        msg.value[sizeof(msg.value) - 1] = '\0';
        const std::string value = msg.value;
        if (msg.kind == win::MsgKind::KeyDown && value == g_config.toggle_key) {
            g_toggle_requested = true;
            continue;
        }
        if (msg.kind == win::MsgKind::KeyUp && value == g_config.toggle_key) continue;
        std::lock_guard lock(g_rec_mu);
        if (!g_recorder) continue;
        switch (msg.kind) {
            case win::MsgKind::KeyDown: g_recorder->on_key(value, true, msg.t_ms); break;
            case win::MsgKind::KeyUp: g_recorder->on_key(value, false, msg.t_ms); break;
            case win::MsgKind::ButtonDown: g_recorder->on_mouse_button(value, true, msg.t_ms); break;
            case win::MsgKind::ButtonUp: g_recorder->on_mouse_button(value, false, msg.t_ms); break;
            case win::MsgKind::MouseMove: g_recorder->on_mouse_move(msg.dx, msg.dy, msg.t_ms); break;
        }
    }
}

void init(HMODULE self) {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(self, path, MAX_PATH);
    g_dll_dir = std::filesystem::path(path).parent_path();
    log("client_extractor loaded, pid " + std::to_string(GetCurrentProcessId()));

    std::string error;
    auto cfg = load_config((g_dll_dir / "odx_config.lua").string(), error);
    if (!cfg) return log("config error: " + error);
    g_config = *cfg;
    g_profile = Profile::load(g_config.profile, g_memory, error);
    if (!g_profile) return log("profile error: " + error);
    log("profile loaded: " + g_profile->info().game);

    std::thread(input_listener).detach();
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
