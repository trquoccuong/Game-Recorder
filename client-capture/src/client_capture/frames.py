"""frames.csv schema, per-frame input aggregation and writing (Capture Spec v2 §1.1.2.6, §1.1.3)."""

import csv
from dataclasses import dataclass
from pathlib import Path

C2W_COLUMNS = [f"c2w_m{r}{c}" for r in range(4) for c in range(4)]
INTRINSIC_COLUMNS = ["camera_fx", "camera_fy", "camera_cx", "camera_cy"]
DISTORTION_COLUMNS = [f"camera_radial_k{i}" for i in range(1, 7)] + ["camera_tangential_p1", "camera_tangential_p2"]
INPUT_COLUMNS = ["input_keys", "input_actions", "input_mouse_buttons", "input_mouse_dx", "input_mouse_dy"]

COLUMNS = ["frame_id", "timestamp_ms", *C2W_COLUMNS, "camera_model", *INTRINSIC_COLUMNS, *DISTORTION_COLUMNS, *INPUT_COLUMNS]

CAMERA_MODELS = ("PINHOLE", "OPENCV", "OPENCV_FISHEYE")

# How a null value is written to CSV. Open question for Client: empty field vs a literal token.
NULL = ""

_PRESS = {"key_down": "keys", "button_down": "buttons", "action_start": "actions"}
_RELEASE = {"key_up": "keys", "button_up": "buttons", "action_end": "actions"}


@dataclass(frozen=True)
class InputEvent:
    """One raw input event. `t` uses the same clock as the frame times passed to aggregate_inputs."""

    t: float
    kind: str  # key_down|key_up|button_down|button_up|action_start|action_end|mouse_move
    value: str | None = None
    dx: float = 0.0
    dy: float = 0.0


def read_input_events(path) -> list[InputEvent]:
    """Read input_events.csv written by odx-input (capture-cs): t_qpc_ms,kind,value,dx,dy."""
    with Path(path).open(newline="") as f:
        return [
            InputEvent(float(r["t_qpc_ms"]), r["kind"], r["value"] or None, float(r["dx"]), float(r["dy"]))
            for r in csv.DictReader(f)
        ]


@dataclass(frozen=True)
class VideoFrame:
    """One output video frame written by odx-record (capture-cs)."""

    index: int
    t_grid: float  # QPC ms of the constant-rate grid tick
    t_source: float  # QPC ms of the captured frame shown (WGC SystemRelativeTime)
    duplicate: bool


def read_video_frames(path) -> list[VideoFrame]:
    """Read video_frames.csv written by odx-record: frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate."""
    with Path(path).open(newline="") as f:
        return [
            VideoFrame(int(r["frame_index"]), float(r["t_grid_qpc_ms"]), float(r["t_source_qpc_ms"]), r["duplicate"] == "1")
            for r in csv.DictReader(f)
        ]


def aggregate_inputs(frame_times, events) -> list[dict]:
    """Fold raw events into one input row per frame.

    Frame i covers events with frame_times[i-1] < t <= frame_times[i]. A key is active in a frame if it is
    held at the frame's start or pressed during it, so taps shorter than a frame are kept. Mouse deltas are summed.
    """
    events = sorted(events, key=lambda e: e.t)
    held = {"keys": {}, "buttons": {}, "actions": {}}  # dicts used as insertion-ordered sets
    rows, j = [], 0
    for t_frame in frame_times:
        active = {k: dict(v) for k, v in held.items()}
        dx = dy = 0.0
        while j < len(events) and events[j].t <= t_frame:
            e = events[j]
            if e.kind in _PRESS:
                held[_PRESS[e.kind]][e.value] = None
                active[_PRESS[e.kind]][e.value] = None
            elif e.kind in _RELEASE:
                held[_RELEASE[e.kind]].pop(e.value, None)
            elif e.kind == "mouse_move":
                dx += e.dx
                dy += e.dy
            else:
                raise ValueError(f"unknown input event kind: {e.kind}")
            j += 1
        rows.append(
            {
                "input_keys": "|".join(active["keys"]) or None,
                "input_actions": "|".join(active["actions"]) or None,
                "input_mouse_buttons": "|".join(active["buttons"]) or None,
                "input_mouse_dx": dx,
                "input_mouse_dy": dy,
            }
        )
    return rows


def _format(value) -> str:
    if value is None:
        return NULL
    if isinstance(value, float):
        return repr(value)  # shortest round-trip repr keeps full precision; 0.0 -> "0.0"
    return str(value)


def write_frames_csv(path, rows) -> None:
    """Write rows (dicts) with every spec column; missing columns are written as null."""
    with Path(path).open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(COLUMNS)
        for row in rows:
            w.writerow([_format(row.get(c)) for c in COLUMNS])


def c2w_row(c2w) -> dict:
    return {name: float(c2w[i // 4][i % 4]) for i, name in enumerate(C2W_COLUMNS)}
