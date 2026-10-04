"""Synthetic session fixtures: one valid reference session plus mutators that each break one v2 §1.5 rule."""

import csv
import json
import shutil
import subprocess
from pathlib import Path

from client_capture.frames import InputEvent, aggregate_inputs, c2w_row, write_frames_csv
from client_capture.geometry import c2w_from_forward, forward_from_heading_pitch, intrinsics_from_vfov

FPS = 60
N_FRAMES = 120
WIDTH, HEIGHT = 320, 180


def make_video(path: Path, n_frames: int = N_FRAMES, width: int = WIDTH, height: int = HEIGHT) -> None:
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", f"testsrc=size={width}x{height}:rate={FPS}",
         "-frames:v", str(n_frames), "-c:v", "libx264", "-bf", "0", "-pix_fmt", "yuv420p", str(path)],
        check=True,
    )


def make_valid_session(session_dir: Path, video: bool = True) -> Path:
    session_dir.mkdir(parents=True, exist_ok=True)
    duration_ms = N_FRAMES * 1000 / FPS
    session = {
        "vendor_name": "odx",
        "game_title": "synthetic",
        "session_id": "fixture-0001",
        "created_at_utc": "2026-10-04T10:00:00Z",
        "ended_at_utc": "2026-10-04T10:00:02Z",
        "duration_ms": duration_ms,
        "duration_seconds": duration_ms / 1000,
        "fps": FPS,
        "frame_count": N_FRAMES,
        "record_width_px": WIDTH,
        "record_height_px": HEIGHT,
        "screen_width_px": 1920,
        "screen_height_px": 1080,
        "localization": "en-US",
        "platform": "PC",
        "input_mouse_convention": {
            "maps_to": "camera_look_velocity",
            "dx_positive": "right",
            "dx_negative": "left",
            "dy_positive": "down",
            "dy_negative": "up",
        },
    }
    (session_dir / "session.json").write_text(json.dumps(session, indent=2))

    times = [round(i * 1000 / FPS) for i in range(N_FRAMES)]
    events = [
        InputEvent(times[10] - 1, "key_down", "W"),
        InputEvent(times[10] - 1, "action_start", "Forward"),
        InputEvent(times[40] - 1, "key_up", "W"),
        InputEvent(times[40] - 1, "action_end", "Forward"),
        *(InputEvent(times[i] - 2, "mouse_move", dx=3.0, dy=-1.0) for i in range(50, 80)),
    ]
    inputs = aggregate_inputs(times, events)
    fx, fy, cx, cy = intrinsics_from_vfov(60.0, WIDTH, HEIGHT)
    rows = []
    for i, t in enumerate(times):
        c2w = c2w_from_forward(forward_from_heading_pitch(i * 0.75, 5.0), (i * 0.1, 0.0, 1.7))
        rows.append({
            "frame_id": i, "timestamp_ms": t, **c2w_row(c2w), "camera_model": "PINHOLE",
            "camera_fx": fx, "camera_fy": fy, "camera_cx": cx, "camera_cy": cy, **inputs[i],
        })
    write_frames_csv(session_dir / "frames.csv", rows)
    if video:
        make_video(session_dir / "video.mp4")
    return session_dir


def _edit_session(d: Path, **changes) -> None:
    s = json.loads((d / "session.json").read_text())
    for k, v in changes.items():
        if v is None:
            s.pop(k, None)
        else:
            s[k] = v
    (d / "session.json").write_text(json.dumps(s))


def _edit_rows(d: Path, fn) -> None:
    with (d / "frames.csv").open(newline="") as f:
        reader = csv.DictReader(f)
        header, rows = reader.fieldnames, list(reader)
    header = fn(rows) or header
    with (d / "frames.csv").open("w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=header, extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)


def _set(i: int, **values):
    def fn(rows):
        rows[i].update({k: str(v) for k, v in values.items()})
    return fn


def _scale(i: int, col: str, factor: float):
    def fn(rows):
        rows[i][col] = repr(float(rows[i][col]) * factor)
    return fn


def _flip_backward(rows):
    for col in ("c2w_m02", "c2w_m12", "c2w_m22"):
        rows[20][col] = repr(-float(rows[20][col]))


def _shift_timestamps(rows):
    for r in rows:
        r["timestamp_ms"] = str(int(r["timestamp_ms"]) + 100)


def _drop_column(rows):
    from client_capture.frames import COLUMNS
    return [c for c in COLUMNS if c != "camera_tangential_p2"]


def _null_camera(rows):
    from client_capture.frames import C2W_COLUMNS
    for c in C2W_COLUMNS:
        rows[30][c] = ""


def _no_input_frame(rows) -> int:
    return next(i for i, r in enumerate(rows) if not r["input_keys"] and r["input_mouse_dx"] == "0.0")


def _key_frame(rows) -> int:
    return next(i for i, r in enumerate(rows) if r["input_keys"])


# rule id -> mutator(session_dir). Each mutator targets exactly that rule (others may fire as a consequence).
MUTATIONS = {
    "session.schema": lambda d: _edit_session(d, platform=None),
    "session.timezone": lambda d: _edit_session(d, created_at_utc="2026-10-04T10:00:00"),
    "session.end_after_start": lambda d: _edit_session(d, ended_at_utc="2026-10-04T09:59:58Z"),
    "session.duration_units": lambda d: _edit_session(d, duration_seconds=4.0),
    "session.duration_timestamps": lambda d: _edit_session(d, ended_at_utc="2026-10-04T10:00:12Z"),
    "session.frame_count_fps": lambda d: _edit_session(d, fps=30),
    "frames.headers": lambda d: _edit_rows(d, _drop_column),
    "frames.row_count": lambda d: _edit_session(d, frame_count=N_FRAMES - 1),
    "frames.frame_id_sequence": lambda d: _edit_rows(d, _set(5, frame_id=7)),
    "frames.timestamp_monotonic": lambda d: _edit_rows(d, lambda rows: rows[50].update(timestamp_ms=rows[49]["timestamp_ms"])),
    "frames.timestamp_start": lambda d: _edit_rows(d, _shift_timestamps),
    "frames.timestamp_end": lambda d: _edit_session(d, duration_ms=1900.0, duration_seconds=1.9),
    "frames.frame_interval": lambda d: _edit_rows(d, lambda rows: rows[60].update(timestamp_ms=str(int(rows[60]["timestamp_ms"]) + 8))),
    "camera.sparse_nulls": lambda d: _edit_rows(d, _null_camera),
    "camera.model": lambda d: _edit_rows(d, _set(0, camera_model="PERSPECTIVE")),
    "camera.principal_point": lambda d: _edit_rows(d, lambda rows: rows[0].update(camera_cx=repr(float(rows[0]["camera_cx"]) + 20))),
    "camera.fx_fy": lambda d: _edit_rows(d, _scale(0, "camera_fx", 1.1)),
    "c2w.bottom_row": lambda d: _edit_rows(d, _set(0, c2w_m30=0.1)),
    "c2w.det": lambda d: _edit_rows(d, _flip_backward),
    "c2w.orthonormal": lambda d: _edit_rows(d, _scale(0, "c2w_m00", 1.5)),
    "input.mouse_delta": lambda d: _edit_rows(d, _set(3, input_mouse_dx="")),
    "input.keys_without_actions": lambda d: _edit_rows(d, lambda rows: rows[_key_frame(rows)].update(input_actions="")),
    "input.no_input_null": lambda d: _edit_rows(d, lambda rows: rows[_no_input_frame(rows)].update(input_actions="Jump")),
    "input.delimiter": lambda d: _edit_rows(d, lambda rows: rows[_key_frame(rows)].update(input_keys="W,Shift")),
    "video.missing": lambda d: (d / "video.mp4").unlink(),
    "video.frame_count": lambda d: make_video(d / "video.mp4", n_frames=N_FRAMES - 10),
    "video.resolution": lambda d: make_video(d / "video.mp4", width=640, height=360),
}


def copy_session(src: Path, dst: Path) -> Path:
    shutil.copytree(src, dst)
    return dst
