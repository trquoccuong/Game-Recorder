// Records the camera for one capture: one camera_samples.csv row per presented frame (written on a separate thread so
// the game's render thread never waits on disk) and extractor.json with the profile's axis fields on stop.
// `odx build` (dotnet/) merges them with the odx input / odx record logs of the same capture.
#pragma once

#include <condition_variable>
#include <cstdint>
#include <deque>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>

#include "odx/profile.hpp"

namespace odx {

// camera_samples.csv header; the row format is pinned by dotnet/fixtures/camera_samples.csv.
extern const char* const kCameraSamplesHeader;

// Shortest round-trip representation, always with a decimal point or exponent ("0.0", "1.5", "1e-05").
std::string format_double(double v);

struct RecorderConfig {
    std::string dir;  // created if missing
    ProfileInfo profile;
};

class Recorder {
public:
    explicit Recorder(RecorderConfig cfg);
    ~Recorder();

    bool start(std::string& error);
    // `t_ms` is QueryPerformanceCounter in milliseconds, the clock of odx input and odx record.
    void on_frame(double t_ms, const CameraSample& sample);
    // Writes extractor.json and closes camera_samples.csv. Returns the number of samples written.
    int64_t stop();
    bool recording() const { return recording_; }

private:
    void writer_loop();

    RecorderConfig cfg_;
    std::ofstream out_;
    std::thread writer_thread_;
    std::mutex mu_;
    std::condition_variable cv_;
    std::deque<std::string> queue_;
    bool recording_ = false;
    bool stopping_ = false;
    int64_t count_ = 0;
};

}  // namespace odx
