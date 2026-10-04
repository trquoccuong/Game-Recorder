// Per-frame input aggregation (Capture Spec v2 §1.1.2.6). Mirrors Odx.Core.Frames.AggregateInputs (capture-cs).
#pragma once

#include <mutex>
#include <string>
#include <vector>

namespace odx {

enum class InputKind { KeyDown, KeyUp, ButtonDown, ButtonUp, ActionStart, ActionEnd, MouseMove };

struct InputEvent {
    double t = 0;  // same clock as the frame times passed to close_frame()
    InputKind kind = InputKind::MouseMove;
    std::string value;
    double dx = 0, dy = 0;
};

// Pipe-delimited fields; an empty string means null.
struct FrameInput {
    std::string keys, actions, buttons;
    double dx = 0, dy = 0;
};

// Thread-safe: push() from the input thread, close_frame() from the frame thread.
// Frame i covers events with t_{i-1} < t <= t_i. A value is active if held at frame start or pressed during
// the frame, so taps shorter than one frame are kept. Mouse deltas are summed.
class InputAggregator {
public:
    void push(InputEvent e);
    FrameInput close_frame(double t);

private:
    using OrderedSet = std::vector<std::string>;
    std::mutex mu_;
    std::vector<InputEvent> pending_;
    OrderedSet keys_, buttons_, actions_;
};

}  // namespace odx
