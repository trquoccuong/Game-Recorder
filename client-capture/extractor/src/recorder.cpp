#include "odx/recorder.hpp"

#include <charconv>
#include <filesystem>

namespace odx {

const char* const kCameraSamplesHeader =
    "t_qpc_ms,pos_x,pos_y,pos_z,fwd_x,fwd_y,fwd_z,right_x,right_y,right_z,up_x,up_y,up_z,fov_deg";

std::string format_double(double v) {
    char buf[64];
    auto [end, ec] = std::to_chars(buf, buf + sizeof(buf), v);
    std::string s(buf, end);
    if (s.find_first_of(".eEn") == std::string::npos) s += ".0";  // "n" covers nan/inf
    return s;
}

namespace {
std::string json_string(const std::string& v) {
    std::string out = "\"";
    for (char c : v) {
        if (c == '"' || c == '\\') out += '\\';
        if (static_cast<unsigned char>(c) >= 0x20) out += c;
    }
    return out + "\"";
}
}  // namespace

Recorder::Recorder(RecorderConfig cfg) : cfg_(std::move(cfg)) {}

Recorder::~Recorder() {
    if (recording_) stop();
}

bool Recorder::start(std::string& error) {
    std::error_code ec;
    std::filesystem::create_directories(cfg_.dir, ec);
    if (ec) {
        error = "cannot create " + cfg_.dir + ": " + ec.message();
        return false;
    }
    out_.open(cfg_.dir + "/camera_samples.csv", std::ios::binary);
    if (!out_) {
        error = "cannot open camera_samples.csv in " + cfg_.dir;
        return false;
    }
    out_ << kCameraSamplesHeader << '\n';
    count_ = 0;
    stopping_ = false;
    recording_ = true;
    writer_thread_ = std::thread([this] { writer_loop(); });
    return true;
}

void Recorder::on_frame(double t_ms, const CameraSample& s) {
    if (!recording_) return;
    std::string line = format_double(t_ms);
    for (const Vec3& v : {s.position, s.forward, s.right, s.up})
        for (double c : {v.x, v.y, v.z}) line += "," + format_double(c);
    line += "," + format_double(s.fov_deg) + "\n";
    ++count_;
    {
        std::lock_guard lock(mu_);
        queue_.push_back(std::move(line));
    }
    cv_.notify_one();
}

void Recorder::writer_loop() {
    std::unique_lock lock(mu_);
    while (true) {
        cv_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
        while (!queue_.empty()) {
            std::string line = std::move(queue_.front());
            queue_.pop_front();
            lock.unlock();
            out_ << line;
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
    out_.close();

    const ProfileInfo& p = cfg_.profile;
    std::ofstream(cfg_.dir + "/extractor.json", std::ios::binary)
        << "{\n"
        << "  \"game\": " << json_string(p.game) << ",\n"
        << "  \"engine\": " << json_string(p.engine) << ",\n"
        << "  \"convention\": " << json_string(p.convention) << ",\n"
        << "  \"units_to_world\": " << format_double(p.units_to_world) << ",\n"
        << "  \"fov_axis\": " << json_string(p.fov_axis) << ",\n"
        << "  \"camera_model\": " << json_string(p.camera_model) << "\n"
        << "}\n";
    return count_;
}

}  // namespace odx
