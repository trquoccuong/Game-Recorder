// odx_synth <profile.lua> <session_dir> [frames]
// Drives the full capture path against the fake game: Lua profile (signature scan + memory reads) -> Recorder
// -> frames.csv + session.json. The output must pass `odx-validate --no-video`.
#include <cstdio>
#include <cstdlib>
#include <string>

#include "odx/fake_game.hpp"
#include "odx/profile.hpp"
#include "odx/recorder.hpp"

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: %s <profile.lua> <session_dir> [frames]\n", argv[0]);
        return 2;
    }
    const int frames = argc > 3 ? std::atoi(argv[3]) : 120;
    const int width = 1280, height = 720;
    const double fps = 60;

    odx::FakeGame game;
    std::string error;
    auto profile = odx::Profile::load(argv[1], game.memory(), error);
    if (!profile || !profile->init()) {
        std::fprintf(stderr, "profile error: %s\n", profile ? profile->last_error().c_str() : error.c_str());
        return 1;
    }

    odx::RecorderConfig cfg;
    cfg.session_dir = argv[2];
    cfg.session_id = "synthetic-0001";
    cfg.fps = fps;
    cfg.profile = profile->info();
    odx::Recorder rec(cfg);
    if (!rec.start(error)) {
        std::fprintf(stderr, "recorder error: %s\n", error.c_str());
        return 1;
    }

    for (int i = 0; i < frames; ++i) {
        double t = 5000.0 + i * 1000.0 / fps;  // arbitrary monotonic clock origin
        game.set_camera(odx::c2w_from_forward(odx::forward_from_heading_pitch(i * 0.75, 5.0), {i * 0.1, 0.0, 1.7}), 90.0);
        if (i == 10) rec.on_key("W", true, t - 1);
        if (i == 40) rec.on_key("W", false, t - 1);
        if (i == 45) rec.on_mouse_button("Left", true, t - 3), rec.on_mouse_button("Left", false, t - 2);
        if (i >= 50 && i < 80) rec.on_mouse_move(3.0, -1.0, t - 2);
        auto cam = profile->sample(width, height);
        if (!cam) {
            std::fprintf(stderr, "sample error at frame %d: %s\n", i, profile->last_error().c_str());
            return 1;
        }
        rec.on_frame(t, width, height, cam);
    }
    std::printf("wrote %lld frames to %s\n", static_cast<long long>(rec.stop()), argv[2]);
    return 0;
}
