// odx_synth <profile.lua> <capture_dir> [frames]
// A synthetic capture of the fake game, as odx-record and the extractor DLL would leave it: video_frames.csv and
// recording.json (60 fps grid, no video) plus camera_samples.csv + extractor.json from the Lua profile reading the
// fake game's memory. ctest runs `odx-build` and `odx-validate --no-video` on it.
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string>

#include "odx/fake_game.hpp"
#include "odx/profile.hpp"
#include "odx/recorder.hpp"

namespace {
constexpr double kPi = 3.14159265358979323846;

// A roll-free pose (compass heading, pitch, position in metres) in the fake game's Unreal axes and centimetres:
// X forward/north, Y right/east, Z up.
odx::CameraSample unreal_pose(double heading_deg, double pitch_deg, odx::Vec3 pos_m) {
    double h = heading_deg * kPi / 180, p = pitch_deg * kPi / 180;
    odx::Vec3 fwd{std::cos(h) * std::cos(p), std::sin(h) * std::cos(p), std::sin(p)};
    odx::Vec3 right{-std::sin(h), std::cos(h), 0};
    // Unreal is left-handed: up = forward x right.
    odx::Vec3 up{fwd.y * right.z - fwd.z * right.y, fwd.z * right.x - fwd.x * right.z, fwd.x * right.y - fwd.y * right.x};
    return {{pos_m.y * 100, pos_m.x * 100, pos_m.z * 100}, fwd, right, up, 90.0};
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: %s <profile.lua> <capture_dir> [frames]\n", argv[0]);
        return 2;
    }
    const std::string dir = argv[2];
    const int frames = argc > 3 ? std::atoi(argv[3]) : 120;
    const double fps = 60, t0 = 5000;

    odx::FakeGame game;
    std::string error;
    auto profile = odx::Profile::load(argv[1], game.memory(), error);
    if (!profile || !profile->init()) {
        std::fprintf(stderr, "profile error: %s\n", profile ? profile->last_error().c_str() : error.c_str());
        return 1;
    }
    odx::Recorder rec({dir, profile->info()});
    if (!rec.start(error)) {
        std::fprintf(stderr, "recorder error: %s\n", error.c_str());
        return 1;
    }
    std::ofstream grid(dir + "/video_frames.csv", std::ios::binary);
    grid << "frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate\n";
    for (int i = 0; i < frames; ++i) {
        double t = t0 + i * 1000.0 / fps;
        game.set_camera(unreal_pose(i * 0.75, 5.0, {i * 0.1, 0.0, 1.7}));
        auto sample = profile->sample();
        if (!sample) {
            std::fprintf(stderr, "sample error at frame %d: %s\n", i, profile->last_error().c_str());
            return 1;
        }
        rec.on_frame(t - 2, *sample);  // Present shortly before the frame is captured
        grid << i << "," << odx::format_double(t) << "," << odx::format_double(t) << ",0\n";
    }
    rec.stop();
    std::ofstream(dir + "/recording.json", std::ios::binary)
        << "{\"capture_id\": \"synthetic-0001\", \"width\": 1280, \"height\": 720, \"screen_width\": 1920, "
        << "\"screen_height\": 1080, \"fps\": 60, \"started_utc\": \"2026-10-04T10:00:00.000Z\", \"started_qpc_ms\": "
        << odx::format_double(t0) << "}\n";
    std::printf("wrote a %d-frame synthetic capture to %s\n", frames, dir.c_str());
    return 0;
}
