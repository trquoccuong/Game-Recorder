#include "odx/recorder.hpp"

#include <chrono>
#include <cmath>
#include <ctime>
#include <filesystem>
#include <fstream>

namespace odx {

std::string iso_utc(double epoch_seconds) {
    auto secs = static_cast<std::time_t>(std::floor(epoch_seconds));
    int ms = static_cast<int>(std::lround((epoch_seconds - std::floor(epoch_seconds)) * 1000.0));
    if (ms == 1000) {
        ++secs;
        ms = 0;
    }
    std::tm tm{};
#ifdef _WIN32
    gmtime_s(&tm, &secs);
#else
    gmtime_r(&secs, &tm);
#endif
    char buf[40];
    std::snprintf(buf, sizeof(buf), "%04d-%02d-%02dT%02d:%02d:%02d.%03dZ", tm.tm_year + 1900, tm.tm_mon + 1,
                  tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec, ms);
    return buf;
}

Recorder::Recorder(RecorderConfig cfg) : cfg_(std::move(cfg)) {}

Recorder::~Recorder() {
    if (recording_) stop();
}

bool Recorder::start(std::string& error) {
    std::error_code ec;
    std::filesystem::create_directories(cfg_.session_dir, ec);
    if (ec) {
        error = "cannot create " + cfg_.session_dir + ": " + ec.message();
        return false;
    }
    if (!writer_.open(cfg_.session_dir + "/frames.csv")) {
        error = "cannot open frames.csv in " + cfg_.session_dir;
        return false;
    }
    frame_count_ = 0;
    stopping_ = false;
    recording_ = true;
    writer_thread_ = std::thread([this] { writer_loop(); });
    return true;
}

void Recorder::on_frame(double t_ms, int width, int height, const std::optional<CameraSample>& camera) {
    if (!recording_) return;
    if (frame_count_ == 0) {
        t0_ms_ = t_ms;
        width_ = width;
        height_ = height;
        created_epoch_s_ = std::chrono::duration<double>(std::chrono::system_clock::now().time_since_epoch()).count();
        created_at_utc_ = iso_utc(created_epoch_s_);
    }
    FrameRow row;
    row.frame_id = frame_count_++;
    row.timestamp_ms = std::llround(t_ms - t0_ms_);
    last_ts_ms_ = row.timestamp_ms;
    if (camera) {
        row.c2w = camera->c2w;
        row.intrinsics = camera->intrinsics;
        row.camera_model = cfg_.profile.camera_model;
    }
    row.input = input_.close_frame(t_ms);
    {
        std::lock_guard lock(mu_);
        queue_.push_back(std::move(row));
    }
    cv_.notify_one();
}

void Recorder::writer_loop() {
    std::unique_lock lock(mu_);
    while (true) {
        cv_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
        while (!queue_.empty()) {
            FrameRow row = std::move(queue_.front());
            queue_.pop_front();
            lock.unlock();
            writer_.write(row);
            lock.lock();
        }
        if (stopping_) return;
    }
}

int64_t Recorder::stop() {
    if (!recording_) return 0;
    recording_ = false;
    {
        std::lock_guard lock(mu_);
        stopping_ = true;
    }
    cv_.notify_one();
    writer_thread_.join();
    writer_.close();

    SessionInfo s;
    s.vendor_name = cfg_.vendor_name;
    s.game_title = cfg_.profile.game;
    s.session_id = cfg_.session_id;
    s.fps = cfg_.fps;
    s.frame_count = frame_count_;
    // Duration covers the last frame's display interval, so timestamp_ms[-1] + 1 frame ≈ duration_ms.
    s.duration_ms = frame_count_ ? static_cast<double>(last_ts_ms_) + 1000.0 / cfg_.fps : 0.0;
    s.created_at_utc = created_at_utc_;
    s.ended_at_utc = iso_utc(created_epoch_s_ + s.duration_ms / 1000.0);
    s.record_width_px = width_;
    s.record_height_px = height_;
    s.screen_width_px = cfg_.screen_width_px ? cfg_.screen_width_px : width_;
    s.screen_height_px = cfg_.screen_height_px ? cfg_.screen_height_px : height_;
    s.mouse = cfg_.profile.mouse;
    std::ofstream(cfg_.session_dir + "/session.json") << session_json(s);
    return frame_count_;
}

void Recorder::on_key(const std::string& key, bool down, double t_ms) {
    input_.push({t_ms, down ? InputKind::KeyDown : InputKind::KeyUp, key});
    if (auto it = cfg_.profile.bindings.find(key); it != cfg_.profile.bindings.end())
        input_.push({t_ms, down ? InputKind::ActionStart : InputKind::ActionEnd, it->second});
}

void Recorder::on_mouse_button(const std::string& button, bool down, double t_ms) {
    input_.push({t_ms, down ? InputKind::ButtonDown : InputKind::ButtonUp, button});
    if (auto it = cfg_.profile.bindings.find(button); it != cfg_.profile.bindings.end())
        input_.push({t_ms, down ? InputKind::ActionStart : InputKind::ActionEnd, it->second});
}

void Recorder::on_mouse_move(double dx, double dy, double t_ms) {
    input_.push({t_ms, InputKind::MouseMove, {}, dx, dy});
}

}  // namespace odx
