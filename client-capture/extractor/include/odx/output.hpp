// Canonical output files: frames.csv (spec v2 §1.1.3) and session.json (spec v2 §1.1.2.1).
// Column order and null encoding must match client_capture/frames.py.
#pragma once

#include <cstdint>
#include <fstream>
#include <optional>
#include <string>
#include <vector>

#include "odx/input.hpp"
#include "odx/math.hpp"

namespace odx {

const std::vector<std::string>& frame_columns();

// Shortest round-trip representation, always with a decimal point or exponent ("0.0", "1.5", "1e-05").
std::string format_double(double v);

struct FrameRow {
    int64_t frame_id = 0;
    int64_t timestamp_ms = 0;
    std::optional<Mat4> c2w;          // nullopt -> camera columns null
    std::optional<Intrinsics> intrinsics;
    std::string camera_model;         // empty -> null
    FrameInput input;
};

class FramesCsvWriter {
public:
    bool open(const std::string& path);
    void write(const FrameRow& row);
    void close();

private:
    std::ofstream out_;
};

struct MouseConvention {
    std::string maps_to = "camera_look_velocity";
    std::string dx_positive = "right", dx_negative = "left";
    std::string dy_positive = "down", dy_negative = "up";
};

struct SessionInfo {
    std::string vendor_name, game_title, session_id;
    std::string created_at_utc, ended_at_utc;  // ISO 8601 with Z
    double duration_ms = 0;
    double fps = 0;
    int64_t frame_count = 0;
    int record_width_px = 0, record_height_px = 0;
    int screen_width_px = 0, screen_height_px = 0;
    std::string localization = "en-US";
    std::string platform = "PC";
    MouseConvention mouse;
};

std::string session_json(const SessionInfo& s);

}  // namespace odx
