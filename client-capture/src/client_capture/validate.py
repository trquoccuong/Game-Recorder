"""Vendor validation checks for one recording (Capture Spec v2 §1.5).

Usage: client-validate <session_dir> [<session_dir> ...] [--json] [--no-video]
A session dir holds session.json, frames.csv and video.mp4. Exit code 1 if any error is found.
"""

import argparse
import csv
import json
import shutil
import subprocess
import sys
from dataclasses import asdict, dataclass
from datetime import datetime
from importlib import resources
from pathlib import Path

import numpy as np
from jsonschema import Draft202012Validator

from .frames import C2W_COLUMNS, CAMERA_MODELS, COLUMNS, INTRINSIC_COLUMNS, NULL
from .geometry import rotation_errors

ROTATION_TOL = 1e-3
PRINCIPAL_POINT_TOL_PX = 4.0
LEVEL_ROLL_TOL = 0.05


@dataclass
class Issue:
    rule: str
    severity: str  # "error" | "warning"
    message: str


def _schema() -> dict:
    return json.loads(resources.files("client_capture").joinpath("schemas/session_schema.json").read_text())


def _num(value):
    return None if value == NULL else float(value)


def _frames_summary(bad_frames: list[int]) -> str:
    return f"{len(bad_frames)} frame(s), first at frame {bad_frames[0]}"


def check_session(session: dict) -> list[Issue]:
    issues = [
        Issue("session.schema", "error", f"{'/'.join(map(str, e.path)) or '<root>'}: {e.message}")
        for e in Draft202012Validator(_schema()).iter_errors(session)
    ]
    try:
        start = datetime.fromisoformat(session["created_at_utc"])
        end = datetime.fromisoformat(session["ended_at_utc"])
    except (KeyError, TypeError, ValueError):
        return issues
    if start.tzinfo is None or end.tzinfo is None:
        issues.append(Issue("session.timezone", "error", "timestamps must be timezone-aware (Z or ±HH:MM)"))
        return issues
    if end <= start:
        issues.append(Issue("session.end_after_start", "error", "ended_at_utc must be after created_at_utc"))
    try:
        duration_ms = float(session["duration_ms"])
        duration_s = float(session["duration_seconds"])
        fps = float(session["fps"])
        frame_count = int(session["frame_count"])
    except (KeyError, TypeError, ValueError):
        return issues
    if abs(duration_ms / 1000 - duration_s) > 1:
        issues.append(Issue("session.duration_units", "error", f"duration_ms/1000={duration_ms / 1000} vs duration_seconds={duration_s}"))
    wall_ms = (end - start).total_seconds() * 1000
    if abs(duration_ms - wall_ms) > 1000:
        issues.append(Issue("session.duration_timestamps", "error", f"duration_ms={duration_ms} vs ended-created={wall_ms}"))
    if abs(frame_count - fps * duration_s) > 2:
        issues.append(Issue("session.frame_count_fps", "error", f"frame_count={frame_count} vs fps*duration={fps * duration_s:.1f}"))
    return issues


def check_frames(session: dict, header: list[str], rows: list[dict]) -> list[Issue]:
    issues = []
    missing = [c for c in COLUMNS if c not in header]
    if missing:
        issues.append(Issue("frames.headers", "error", f"missing columns: {', '.join(missing)}"))
        return issues
    if len(rows) != session.get("frame_count"):
        issues.append(Issue("frames.row_count", "error", f"{len(rows)} rows vs frame_count={session.get('frame_count')}"))
    if not rows:
        return issues

    ids = [int(r["frame_id"]) for r in rows]
    if ids != list(range(len(rows))):
        bad = [i for i, v in enumerate(ids) if v != i]
        issues.append(Issue("frames.frame_id_sequence", "error", f"frame_id not 0..N-1: {_frames_summary(bad)}"))

    ts = [float(r["timestamp_ms"]) for r in rows]
    interval = 1000.0 / float(session.get("fps") or 1)
    dts = np.diff(ts)
    if np.any(dts <= 0):
        bad = [int(i) + 1 for i in np.nonzero(dts <= 0)[0]]
        issues.append(Issue("frames.timestamp_monotonic", "error", f"timestamp_ms not increasing: {_frames_summary(bad)}"))
    if abs(ts[0]) > interval:
        issues.append(Issue("frames.timestamp_start", "error", f"timestamp_ms[0]={ts[0]} should be ≈ 0"))
    duration_ms = session.get("duration_ms")
    if isinstance(duration_ms, (int, float)) and abs(ts[-1] - duration_ms) > interval + 1:
        issues.append(Issue("frames.timestamp_end", "error", f"timestamp_ms[-1]={ts[-1]} vs duration_ms={duration_ms}"))
    irregular = np.abs(dts - interval) > max(1.0, 0.25 * interval)
    if np.any(irregular & (dts > 0)):
        bad = [int(i) + 1 for i in np.nonzero(irregular & (dts > 0))[0]]
        issues.append(Issue("frames.frame_interval", "error", f"spacing deviates from {interval:.2f} ms: {_frames_summary(bad)}"))

    issues += _check_camera(session, rows)
    issues += _check_input(rows)
    return issues


def _check_camera(session: dict, rows: list[dict]) -> list[Issue]:
    issues = []
    camera_cols = [*C2W_COLUMNS, "camera_model", *INTRINSIC_COLUMNS]
    has_camera = [any(r[c] != NULL for c in camera_cols) for r in rows]
    if not any(has_camera):
        return issues  # session without camera data: all camera columns null
    sparse = [i for i, r in enumerate(rows) if any(r[c] == NULL for c in camera_cols)]
    if sparse:
        issues.append(Issue("camera.sparse_nulls", "error", f"camera columns null mid-session: {_frames_summary(sparse)}"))

    bad = {k: [] for k in ("model", "bottom_row", "det", "orthonormal", "principal_point", "fx_fy", "roll", "upright")}
    width, height = session.get("record_width_px"), session.get("record_height_px")
    for i, r in enumerate(rows):
        if r["camera_model"] != NULL and r["camera_model"] not in CAMERA_MODELS:
            bad["model"].append(i)
        if any(r[c] == NULL for c in C2W_COLUMNS):
            continue
        m = np.array([float(r[c]) for c in C2W_COLUMNS]).reshape(4, 4)
        if not (m[3, 0] == 0 and m[3, 1] == 0 and m[3, 2] == 0 and m[3, 3] == 1):
            bad["bottom_row"].append(i)
        det_err, ortho_err = rotation_errors(m)
        if det_err > ROTATION_TOL:
            bad["det"].append(i)
        if ortho_err > ROTATION_TOL:
            bad["orthonormal"].append(i)
        if abs(m[2, 0]) > LEVEL_ROLL_TOL:
            bad["roll"].append(i)
        if m[2, 1] <= 0:
            bad["upright"].append(i)
        if any(r[c] == NULL for c in INTRINSIC_COLUMNS):
            continue
        fx, fy, cx, cy = (float(r[c]) for c in INTRINSIC_COLUMNS)
        if (width and abs(cx - width / 2) > PRINCIPAL_POINT_TOL_PX) or (height and abs(cy - height / 2) > PRINCIPAL_POINT_TOL_PX):
            bad["principal_point"].append(i)
        if abs(fx - fy) > 1e-3 * abs(fy):
            bad["fx_fy"].append(i)

    rules = {
        "model": ("camera.model", "error", f"camera_model must be one of {', '.join(CAMERA_MODELS)}"),
        "bottom_row": ("c2w.bottom_row", "error", "bottom row must be exactly (0, 0, 0, 1)"),
        "det": ("c2w.det", "error", "det(R) must be +1 (right-handed)"),
        "orthonormal": ("c2w.orthonormal", "error", "R·Rᵀ must be identity"),
        "principal_point": ("camera.principal_point", "error", "principal point must be near the image centre"),
        "fx_fy": ("camera.fx_fy", "error", "fx must equal fy for square pixels"),
        "roll": ("c2w.roll", "warning", "c2w_m20 not ≈ 0 (camera roll)"),
        "upright": ("c2w.upright", "warning", "c2w_m21 <= 0 (camera not upright)"),
    }
    for key, frames in bad.items():
        if frames:
            rule, severity, msg = rules[key]
            issues.append(Issue(rule, severity, f"{msg}: {_frames_summary(frames)}"))
    return issues


def _check_input(rows: list[dict]) -> list[Issue]:
    bad = {k: [] for k in ("delta", "keys_without_actions", "no_input_null", "delimiter")}
    for i, r in enumerate(rows):
        try:
            dx, dy = _num(r["input_mouse_dx"]), _num(r["input_mouse_dy"])
        except ValueError:
            dx = dy = None
        if dx is None or dy is None:
            bad["delta"].append(i)
        keys, actions, buttons = r["input_keys"], r["input_actions"], r["input_mouse_buttons"]
        if keys != NULL and actions == NULL:
            bad["keys_without_actions"].append(i)
        if keys == NULL and buttons == NULL and dx == 0 and dy == 0 and actions != NULL:
            bad["no_input_null"].append(i)
        for value in (keys, actions, buttons):
            if value != NULL and ("," in value or any(tok.strip() == "" for tok in value.split("|"))):
                bad["delimiter"].append(i)
                break

    rules = {
        "delta": ("input.mouse_delta", "input_mouse_dx/dy must be numeric, \"0.0\" when no movement"),
        "keys_without_actions": ("input.keys_without_actions", "active input_keys need non-null input_actions"),
        "no_input_null": ("input.no_input_null", "frames with no input must have null input_actions"),
        "delimiter": ("input.delimiter", "multi-value fields must be pipe-delimited with no empty tokens"),
    }
    return [Issue(rules[k][0], "error", f"{rules[k][1]}: {_frames_summary(v)}") for k, v in bad.items() if v]


def probe_video(path: Path) -> dict:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets",
         "-show_entries", "stream=width,height,nb_read_packets:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, check=True,
    )
    data = json.loads(out.stdout)
    stream = data["streams"][0]
    return {
        "width": int(stream["width"]),
        "height": int(stream["height"]),
        "frames": int(stream["nb_read_packets"]),
        "duration": float(data["format"]["duration"]),
    }


def check_video(session: dict, n_rows: int, video_path: Path) -> list[Issue]:
    if not video_path.exists():
        return [Issue("video.missing", "error", f"{video_path.name} not found")]
    if shutil.which("ffprobe") is None:
        return [Issue("video.ffprobe_unavailable", "warning", "ffprobe not installed; video checks skipped")]
    try:
        v = probe_video(video_path)
    except (subprocess.CalledProcessError, KeyError, IndexError, ValueError) as e:
        return [Issue("video.unreadable", "error", f"ffprobe failed: {e}")]
    issues = []
    if (v["width"], v["height"]) != (session.get("record_width_px"), session.get("record_height_px")):
        issues.append(Issue("video.resolution", "error", f"video {v['width']}x{v['height']} vs session {session.get('record_width_px')}x{session.get('record_height_px')}"))
    if v["frames"] != n_rows:
        issues.append(Issue("video.frame_count", "error", f"video has {v['frames']} frames vs {n_rows} frames.csv rows"))
    duration_s = session.get("duration_seconds")
    if isinstance(duration_s, (int, float)) and abs(v["duration"] - duration_s) > 1:
        issues.append(Issue("video.duration", "error", f"video duration {v['duration']:.2f}s vs duration_seconds={duration_s}"))
    return issues


def validate_session(session_dir, video: bool = True) -> list[Issue]:
    session_dir = Path(session_dir)
    try:
        session = json.loads((session_dir / "session.json").read_text())
    except (OSError, json.JSONDecodeError) as e:
        return [Issue("session.unreadable", "error", str(e))]
    issues = check_session(session)
    try:
        with (session_dir / "frames.csv").open(newline="") as f:
            reader = csv.DictReader(f)
            rows = list(reader)
            header = reader.fieldnames or []
    except OSError as e:
        return issues + [Issue("frames.unreadable", "error", str(e))]
    try:
        issues += check_frames(session, header, rows)
    except (ValueError, KeyError) as e:
        issues.append(Issue("frames.unparseable", "error", f"could not parse frames.csv: {e}"))
    if video:
        issues += check_video(session, len(rows), session_dir / "video.mp4")
    return issues


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Validate recordings against Client Capture Spec v2 §1.5.")
    parser.add_argument("sessions", nargs="+", help="session directories")
    parser.add_argument("--json", action="store_true", help="print machine-readable results")
    parser.add_argument("--no-video", action="store_true", help="skip video.mp4 checks")
    args = parser.parse_args(argv)

    results = {s: validate_session(s, video=not args.no_video) for s in args.sessions}
    if args.json:
        print(json.dumps({s: [asdict(i) for i in issues] for s, issues in results.items()}, indent=2, ensure_ascii=False))
    else:
        for s, issues in results.items():
            errors = sum(i.severity == "error" for i in issues)
            print(f"{'FAIL' if errors else 'PASS'}  {s}  ({errors} errors, {len(issues) - errors} warnings)")
            for i in issues:
                print(f"  [{i.severity}] {i.rule}: {i.message}")
    return 1 if any(i.severity == "error" for issues in results.values() for i in issues) else 0


if __name__ == "__main__":
    sys.exit(main())
