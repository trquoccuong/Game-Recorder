// Records one session: per-frame camera + input rows to frames.csv (writer thread), session.json on stop.
#pragma once

#include <atomic>
#include <condition_variable>
#include <deque>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "odx/input.hpp"
#include "odx/output.hpp"
#include "odx/profile.hpp"

namespace odx {

struct RecorderConfig {
    std::string session_dir;  // created if missing
    std::string vendor_name = "odx";
    std::string session_id;
    double fps = 60;
    int screen_width_px = 0, screen_height_px = 0;
    ProfileInfo profile;
};

class Recorder {
public:
    explicit Recorder(RecorderConfig cfg);
    ~Recorder();

    bool start(std::string& error);
    // Called once per presented frame. `t_ms` is a monotonic clock shared with the input callbacks.
    void on_frame(double t_ms, int width, int height, const std::optional<CameraSample>& camera);
    // Writes session.json and closes frames.csv. Returns the number of frames written.
    int64_t stop();

    // Input callbacks (any thread). Profile bindings turn keys/buttons into semantic actions.
    void on_key(const std::string& key, bool down, double t_ms);
    void on_mouse_button(const std::string& button, bool down, double t_ms);
    void on_mouse_move(double dx, double dy, double t_ms);

    bool recording() const { return recording_; }

private:
    void writer_loop();

    RecorderConfig cfg_;
    InputAggregator input_;
    FramesCsvWriter writer_;
    std::thread writer_thread_;
    std::mutex mu_;
    std::condition_variable cv_;
    std::deque<FrameRow> queue_;
    std::atomic<bool> recording_ = false;
    bool stopping_ = false;

    int64_t frame_count_ = 0;
    double t0_ms_ = 0;
    int64_t last_ts_ms_ = 0;
    int width_ = 0, height_ = 0;
    std::string created_at_utc_;
    double created_epoch_s_ = 0;
};

// ISO 8601 UTC with milliseconds, e.g. "2026-10-04T10:00:00.123Z".
std::string iso_utc(double epoch_seconds);

}  // namespace odx
