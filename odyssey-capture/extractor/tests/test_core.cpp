#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include <doctest/doctest.h>

#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <random>
#include <sstream>

#include "odx/config.hpp"
#include "odx/fake_game.hpp"
#include "odx/input.hpp"
#include "odx/memory.hpp"
#include "odx/output.hpp"
#include "odx/profile.hpp"
#include "odx/recorder.hpp"

using namespace odx;

namespace {
void check_vec(Vec3 a, Vec3 b, double eps = 1e-12) {
    CHECK(a.x == doctest::Approx(b.x).epsilon(eps));
    CHECK(a.y == doctest::Approx(b.y).epsilon(eps));
    CHECK(a.z == doctest::Approx(b.z).epsilon(eps));
}
Vec3 col(const Mat4& m, int c) { return {m[0][c], m[1][c], m[2][c]}; }
std::string profile_path() { return std::string(ODX_PROFILES_DIR) + "/synthetic.lua"; }
}  // namespace

TEST_CASE("engine camera facing forward maps to north") {
    struct Case {
        Convention c;
        Vec3 right, up, forward;
    };
    for (auto [c, r, u, f] : {Case{Convention::RFU, {1, 0, 0}, {0, 0, 1}, {0, 1, 0}},
                              Case{Convention::Unity, {1, 0, 0}, {0, 1, 0}, {0, 0, 1}},
                              Case{Convention::Unreal, {0, 1, 0}, {0, 0, 1}, {1, 0, 0}},
                              Case{Convention::Quake, {0, -1, 0}, {0, 0, 1}, {1, 0, 0}}}) {
        Mat4 m = c2w_from_engine(r, u, f, {0, 0, 0}, c);
        check_vec(col(m, 0), {1, 0, 0});
        check_vec(col(m, 1), {0, 0, 1});
        check_vec(col(m, 2), {0, -1, 0});
        CHECK(rotation_errors(m).det < 1e-12);
    }
}

TEST_CASE("roll-free cameras are proper rotations with level right axis") {
    std::mt19937 rng(42);
    std::uniform_real_distribution<double> heading(-180, 180), pitch(-89, 89);
    for (int i = 0; i < 1000; ++i) {
        Mat4 m = c2w_from_forward(forward_from_heading_pitch(heading(rng), pitch(rng)), {1, 2, 3});
        auto e = rotation_errors(m);
        CHECK(e.det < 1e-9);
        CHECK(e.ortho < 1e-9);
        CHECK(std::abs(m[2][0]) < 1e-9);
        CHECK(m[2][1] > 0);
    }
}

TEST_CASE("intrinsics") {
    auto k = intrinsics_from_vfov(90, 1920, 1080);
    CHECK(k.fy == doctest::Approx(540));
    CHECK(k.fx == k.fy);
    CHECK(k.cx == 960);
    CHECK(k.cy == 540);
    auto h = intrinsics_from_hfov(90, 1920, 1080);
    CHECK(h.fx == doctest::Approx(960));
}

TEST_CASE("input aggregation matches Odx.Core.Frames semantics") {
    InputAggregator agg;
    agg.push({10, InputKind::KeyDown, "W"});
    agg.push({20, InputKind::KeyDown, "Space"});
    agg.push({25, InputKind::KeyUp, "Space"});  // tap inside one frame
    agg.push({45, InputKind::KeyUp, "W"});
    for (double t : {18.0, 20.0, 30.0, 33.0}) agg.push({t, InputKind::MouseMove, {}, 1.5, -0.5});
    std::vector<FrameInput> rows;
    for (double t : {0.0, 17.0, 33.0, 50.0, 67.0}) rows.push_back(agg.close_frame(t));
    CHECK(rows[0].keys == "");
    CHECK(rows[1].keys == "W");
    CHECK(rows[2].keys == "W|Space");
    CHECK(rows[3].keys == "W");
    CHECK(rows[4].keys == "");
    CHECK(rows[2].dx == 6.0);
    CHECK(rows[2].dy == -2.0);
    CHECK(rows[3].dx == 0.0);
}

TEST_CASE("format_double keeps full precision and a decimal point") {
    CHECK(format_double(0.0) == "0.0");
    CHECK(format_double(3.0) == "3.0");
    CHECK(format_double(-1.5) == "-1.5");
    CHECK(std::stod(format_double(0.1 + 0.2)) == 0.1 + 0.2);
    CHECK(frame_columns().size() == 2 + 16 + 5 + 8 + 5);
}

TEST_CASE("pattern parsing, scan and RIP resolution") {
    CHECK_FALSE(parse_pattern("48 8B zz").has_value());
    CHECK(parse_pattern("48 ?? 05")->size() == 3);

    FakeGame game;
    auto span = game.memory().module("game.exe");
    REQUIRE(span);
    auto hit = scan(game.memory(), *span, *parse_pattern("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ??"));
    REQUIRE(hit);
    CHECK(*hit == span->base + 0x100);
    auto global = resolve_rip(game.memory(), *hit, 3, 7);
    REQUIRE(global);
    CHECK(*global == span->base + 0x1000);
    CHECK_FALSE(scan(game.memory(), *span, *parse_pattern("DE AD BE EF")).has_value());
}

TEST_CASE("Lua profile reads the fake game and reproduces the canonical pose") {
    FakeGame game;
    std::string error;
    auto profile = Profile::load(profile_path(), game.memory(), error);
    REQUIRE_MESSAGE(profile, error);
    REQUIRE_MESSAGE(profile->init(), profile->last_error());
    CHECK(profile->info().bindings.at("W") == "Forward");

    Mat4 expected = c2w_from_forward(forward_from_heading_pitch(37, -12), {4.5, -2.0, 1.7});
    game.set_camera(expected, 90);
    auto cam = profile->sample(1920, 1080);
    REQUIRE_MESSAGE(cam, profile->last_error());
    for (int r = 0; r < 4; ++r)
        for (int c = 0; c < 4; ++c) CHECK(cam->c2w[r][c] == doctest::Approx(expected[r][c]).epsilon(1e-5));
    CHECK(cam->intrinsics.fx == doctest::Approx(960).epsilon(1e-5));  // 90° horizontal at 1920 px
}

TEST_CASE("profile errors are reported, not thrown") {
    BufferMemory empty;
    std::string error;
    auto profile = Profile::load(profile_path(), empty, error);
    REQUIRE(profile);
    CHECK_FALSE(profile->init());  // module game.exe missing -> Lua error
    CHECK(profile->last_error().find("module not found") != std::string::npos);
    CHECK_FALSE(Profile::load("/nonexistent.lua", empty, error));
}

TEST_CASE("config resolves relative paths against its directory") {
    auto dir = std::filesystem::temp_directory_path() / "odx_config_test";
    std::filesystem::create_directories(dir);
    std::ofstream(dir / "odx_config.lua") << "return { profile = 'profiles/x.lua', fps = 30 }";
    std::string error;
    auto cfg = load_config((dir / "odx_config.lua").string(), error);
    REQUIRE_MESSAGE(cfg, error);
    CHECK(cfg->profile == (dir / "profiles/x.lua").string());
    CHECK(cfg->fps == 30);
    CHECK(cfg->toggle_key == "F9");
}

TEST_CASE("recorder writes one CSV row per frame and a consistent session.json") {
    auto dir = std::filesystem::temp_directory_path() / "odx_recorder_test";
    std::filesystem::remove_all(dir);
    RecorderConfig cfg;
    cfg.session_dir = dir.string();
    cfg.session_id = "t";
    cfg.profile.game = "Test";
    cfg.profile.bindings = {{"W", "Forward"}};
    Recorder rec(cfg);
    std::string error;
    REQUIRE(rec.start(error));
    rec.on_key("W", true, 1000);
    for (int i = 0; i < 10; ++i) rec.on_frame(1000 + i * 1000.0 / 60, 640, 360, std::nullopt);
    CHECK(rec.stop() == 10);

    std::ifstream csv(dir / "frames.csv");
    std::string line;
    int n = -1;
    std::string first;
    while (std::getline(csv, line)) {
        if (n == 0) first = line;
        ++n;
    }
    CHECK(n == 10);
    CHECK(first.find(",W,Forward,,0.0,0.0") != std::string::npos);
    std::stringstream session;
    session << std::ifstream(dir / "session.json").rdbuf();
    CHECK(session.str().find("\"frame_count\": 10") != std::string::npos);
}
