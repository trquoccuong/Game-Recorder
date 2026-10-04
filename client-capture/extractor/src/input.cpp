#include "odx/input.hpp"

#include <algorithm>

namespace odx {

namespace {
void add(std::vector<std::string>& set, const std::string& v) {
    if (std::find(set.begin(), set.end(), v) == set.end()) set.push_back(v);
}
void remove(std::vector<std::string>& set, const std::string& v) { std::erase(set, v); }
std::string join(const std::vector<std::string>& set) {
    std::string out;
    for (const auto& v : set) {
        if (!out.empty()) out += '|';
        out += v;
    }
    return out;
}
}  // namespace

void InputAggregator::push(InputEvent e) {
    std::lock_guard lock(mu_);
    pending_.push_back(std::move(e));
}

FrameInput InputAggregator::close_frame(double t) {
    std::lock_guard lock(mu_);
    std::stable_sort(pending_.begin(), pending_.end(), [](const auto& a, const auto& b) { return a.t < b.t; });
    auto active_keys = keys_, active_buttons = buttons_, active_actions = actions_;
    FrameInput out;
    size_t n = 0;
    for (; n < pending_.size() && pending_[n].t <= t; ++n) {
        const auto& e = pending_[n];
        switch (e.kind) {
            case InputKind::KeyDown: add(keys_, e.value); add(active_keys, e.value); break;
            case InputKind::KeyUp: remove(keys_, e.value); break;
            case InputKind::ButtonDown: add(buttons_, e.value); add(active_buttons, e.value); break;
            case InputKind::ButtonUp: remove(buttons_, e.value); break;
            case InputKind::ActionStart: add(actions_, e.value); add(active_actions, e.value); break;
            case InputKind::ActionEnd: remove(actions_, e.value); break;
            case InputKind::MouseMove: out.dx += e.dx; out.dy += e.dy; break;
        }
    }
    pending_.erase(pending_.begin(), pending_.begin() + static_cast<std::ptrdiff_t>(n));
    out.keys = join(active_keys);
    out.buttons = join(active_buttons);
    out.actions = join(active_actions);
    return out;
}

}  // namespace odx
