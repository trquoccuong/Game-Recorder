"""Arma Reforger camera spike: what the mod's camera log means, and how its clock maps onto odx record's.

  analyze.py <camera_spike.csv> [--capture <odx record folder>]
  analyze.py --selftest

Layout: which rows of GetWorldCameraTransform are right / up / forward, found by projecting the logged points with every
assignment and comparing with the engine's own ProjWorldToScreenNative; then fx, fy, cx, cy fitted freely against the
logged vertical FOV. Clock: the flash frames found in video.mp4 (top-left corner) paired with the log's flash ticks, and
QPC = a * tick + b fitted over them.
"""
import argparse
import csv
import itertools
import json
import math
import os
import subprocess
import sys
import tempfile

import numpy as np

POINTS = 12
CORNER_PX = 32
PAIR_TOLERANCE_MS = 100  # a flash lasts one game frame (25 ms at 40 fps) and shows up to a video frame later


def load_log(path):
    with open(path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        sys.exit(f"{path}: no frames")

    def col(name):
        return np.array([float(r[name]) for r in rows])

    log = {k: col(k) for k in ["frame", "tick_ms", "flash", "vfov_deg", "screen_w", "screen_h", "render_w", "render_h"]}
    log["rows"] = np.stack([np.stack([col(f"m{r}{a}") for a in "xyz"], axis=1) for r in range(4)], axis=1)  # (n, 4, 3)
    log["d"] = np.stack([np.stack([col(f"p{k}_d{a}") for a in "xyz"], axis=1) for k in range(POINTS)], axis=1)  # (n, k, 3)
    log["s"] = np.stack([np.stack([col(f"p{k}_s{a}") for a in "xyz"], axis=1) for k in range(POINTS)], axis=1)
    return log


def candidates():
    """Every way to read right / up / forward from the 3x3 block: rows or columns, any order, any signs."""
    for transposed in (False, True):
        for order in itertools.permutations(range(3)):
            for signs in itertools.product((1, -1), repeat=3):
                yield transposed, order, signs


def basis(rot, transposed, order, signs):
    m = np.swapaxes(rot, -1, -2) if transposed else rot
    return np.stack([signs[i] * m[:, order[i], :] for i in range(3)], axis=1)  # (n, 3 [right, up, forward], 3)


def camera_space(log, b):
    return np.einsum("nax,nkx->nka", b, log["d"])  # (n, k, 3): x right, y up, z forward


def visible(log):
    s, w, h = log["s"], log["screen_w"][:, None], log["screen_h"][:, None]
    return (s[..., 2] > 0) & (s[..., 0] >= 0) & (s[..., 0] <= w) & (s[..., 1] >= 0) & (s[..., 1] <= h)


def layout(log):
    rot = log["rows"][:, :3, :]
    mask = visible(log)
    mask[0] = False  # in the game, the first frame's engine projection is not the logged camera's yet
    zoomed = log["vfov_deg"] < np.median(log["vfov_deg"]) - 1
    if mask.sum() < 20:
        sys.exit("fewer than 20 visible point samples: look around the spawn point while recording")
    w, h = log["screen_w"][:, None], log["screen_h"][:, None]
    fy = (h / 2) / np.tan(np.radians(log["vfov_deg"][:, None]) / 2)
    results = []
    for cand in candidates():
        c = camera_space(log, basis(rot, *cand))
        z = c[..., 2]
        ok = mask & (z > 0.1)
        if ok.sum() < mask.sum() * 0.9:
            continue
        u = w / 2 + fy * c[..., 0] / z
        v = h / 2 - fy * c[..., 1] / z
        err = np.hypot(u - log["s"][..., 0], v - log["s"][..., 1])[ok]
        results.append((float(np.sqrt(np.mean(err ** 2))), cand))
    if not results:
        sys.exit("no assignment puts the visible points in front of the camera")
    results.sort(key=lambda r: r[0])
    rms, (transposed, order, signs) = results[0]
    b = basis(rot, transposed, order, signs)
    c = camera_space(log, b)
    ok = mask & (c[..., 2] > 0.1)
    at_hip = ok & ~zoomed[:, None]  # one f for every sample: the FOV must not change

    def fit(proj, screen):  # screen = centre + f * proj
        a = np.stack([proj[at_hip], np.ones(at_hip.sum())], axis=1)
        (f, centre), *_ = np.linalg.lstsq(a, screen[at_hip], rcond=None)
        return float(f), float(centre), float(np.sqrt(np.mean((a @ [f, centre] - screen[at_hip]) ** 2)))

    fx, cx, rx = fit(c[..., 0] / c[..., 2], log["s"][..., 0])
    fy_fit, cy, ry = fit(-c[..., 1] / c[..., 2], log["s"][..., 1])
    h_med = float(np.median(log["screen_h"]))
    det = np.linalg.det(rot)
    names = ["right", "up", "forward"]
    return {
        "samples": int(ok.sum()),
        "frames": len(log["frame"]),
        "read_as": "columns" if transposed else "rows",
        "axes": {names[i]: f"{'-' if signs[i] < 0 else ''}{'col' if transposed else 'row'}{order[i]}" for i in range(3)},
        "rms_px_with_logged_vfov": round(rms, 3),
        "runner_up_rms_px": round(results[1][0], 3) if len(results) > 1 else None,
        "fit": {"fx": round(fx, 2), "fy": round(fy_fit, 2), "cx": round(cx, 2), "cy": round(cy, 2),
                "rms_px": round(math.hypot(rx, ry), 3), "fx_over_fy": round(fx / fy_fit, 5)},
        "vfov_logged_deg": round(float(np.median(log["vfov_deg"])), 4),
        "vfov_logged_range_deg": [round(float(log["vfov_deg"].min()), 4), round(float(log["vfov_deg"].max()), 4)],
        "zoomed_samples": int((ok & zoomed[:, None]).sum()),
        "vfov_fitted_deg": round(math.degrees(2 * math.atan(h_med / 2 / fy_fit)), 4),
        "screen": [float(np.median(log["screen_w"])), h_med],
        "render": [float(np.median(log["render_w"])), float(np.median(log["render_h"]))],
        "det_rows_012": [round(float(det.min()), 4), round(float(det.max()), 4)],
        "orthonormal_max_err": round(float(np.abs(np.einsum("nij,nkj->nik", rot, rot) - np.eye(3)).max()), 6),
        "depth_vs_forward": round(float(np.median(log["s"][..., 2][ok] / c[..., 2][ok])), 4),
    }


def flash_interval_ms(index):
    """Same sequence as the mod's FlashIntervalMs: 4-6 s, never twice the same in a row."""
    return 4000 + (index * 1237) % 2000


def corner_luma(video, x=0, y=0):
    """Mean luma of the CORNER_PX square at (x, y) of every frame."""
    out = subprocess.run(["ffmpeg", "-v", "error", "-i", video, "-vf",
                          f"crop={CORNER_PX}:{CORNER_PX}:{x}:{y},scale=1:1:flags=area,format=gray", "-f", "rawvideo", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(out, dtype=np.uint8).astype(float)


def clock(log, capture, corner=(0, 0)):
    with open(os.path.join(capture, "video_frames.csv"), newline="") as f:
        t_qpc = np.array([float(r["t_grid_qpc_ms"]) for r in csv.DictReader(f)])
    luma = corner_luma(os.path.join(capture, "video.mp4"), *corner)
    n = min(len(luma), len(t_qpc))
    luma, t_qpc = luma[:n], t_qpc[:n]
    lit = luma > (luma.min() + luma.max()) / 2
    video_flashes = t_qpc[1:][lit[1:] & ~lit[:-1]]
    log_flashes = log["tick_ms"][log["flash"] > 0]
    if len(video_flashes) < 2 or len(log_flashes) < 2:
        sys.exit(f"need 2+ flashes on both sides: video {len(video_flashes)}, log {len(log_flashes)}")
    # The log and the video start at different times, and a one-frame flash can be missing from the video (dropped by
    # the recorder): pair each video flash with the log flash nearest to it under the offset that pairs the most. The
    # intervals differ (flash_interval_ms), so a wrong offset pairs only by chance.
    best = None
    for offset in (video_flashes[:, None] - log_flashes[None, :]).ravel():
        gap = np.abs(video_flashes[:, None] - offset - log_flashes[None, :])
        nearest = gap.argmin(axis=1)
        paired = gap[np.arange(len(video_flashes)), nearest] <= PAIR_TOLERANCE_MS
        score = (int(paired.sum()), -float(gap.min(axis=1)[paired].sum()))
        if best is None or score > best[0]:
            best = (score, paired, nearest)
    _, paired, nearest = best
    if paired.sum() < min(3, len(video_flashes)):
        sys.exit(f"could not pair the flashes: {int(paired.sum())} of {len(video_flashes)}")
    ticks, qpc = log_flashes[nearest[paired]], video_flashes[paired]
    a, b = np.polyfit(ticks, qpc, 1)
    resid = qpc - (a * ticks + b)
    frame_ms = float(np.median(np.diff(t_qpc)))
    lit_runs = np.diff(np.flatnonzero(np.diff(np.concatenate([[0], lit.astype(int), [0]]))))[::2]
    return {
        "pairs": int(paired.sum()),
        "video_flashes": len(video_flashes),
        "qpc_ms_per_tick_ms": round(float(a), 7),
        "qpc_at_tick_0_ms": round(float(b), 3),
        "residual_ms": {"max_abs": round(float(np.abs(resid).max()), 3), "std": round(float(resid.std()), 3)},
        "video_frame_ms": round(frame_ms, 3),
        "residual_frames_max": round(float(np.abs(resid).max() / frame_ms), 3),
        "flash_lit_frames": sorted(set(int(x) for x in lit_runs)),
    }


def selftest():
    """Synthetic log (rows = right, up, forward, left-handed, Y up) and video; the analysis must find both back."""
    rng = np.random.default_rng(1)
    tmp = tempfile.mkdtemp(prefix="reforger-spike-")
    w, h, vfov, fps = 1920.0, 1080.0, 74.0, 60
    origin = np.array([1000.0, 50.0, 2000.0])
    points = origin + np.array([[r * math.cos(math.radians(30 * i)), (i % 3 - 1) * 2.0, r * math.sin(math.radians(30 * i))]
                       for i, r in enumerate([8, 30] * 6)])
    fy = (h / 2) / math.tan(math.radians(vfov) / 2)
    tick0, frames = 51234, 2400
    header = "frame,tick_ms,timeslice_s,flash,camera_index,proj_type," + ",".join(
        f"m{r}x,m{r}y,m{r}z" for r in range(4)) + ",vfov_deg,near_m,far_m,screen_w,screen_h,render_w,render_h," + ",".join(
        f"p{k}_dx,p{k}_dy,p{k}_dz,p{k}_sx,p{k}_sy,p{k}_sz" for k in range(POINTS))
    lines, next_flash, flash_ticks = [header], 0, []
    for i in range(frames):
        tick = tick0 + round(i * 1000 / fps)
        yaw, pitch = 2 * math.pi * i / frames * 2, 0.2 * math.sin(i / 50)
        fwd = np.array([math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw)])
        right = np.cross([0, 1, 0], fwd)  # left-handed: right = up x forward
        right /= np.linalg.norm(right)
        up = np.cross(fwd, right)
        pos = origin + rng.normal(0, 0.01, 3)
        flash = tick >= next_flash
        if flash:
            next_flash = tick + flash_interval_ms(len(flash_ticks))
            flash_ticks.append(tick)
        row = [i, tick, 1 / fps, int(flash), 0, 0, *right, *up, *fwd, *pos, vfov, 0.1, 3000, w, h, 1920, 1080]
        for p in points:
            d = p - pos
            x, y, z = d @ right, d @ up, d @ fwd
            s = (w / 2 + fy * x / z, h / 2 - fy * y / z, z) if z > 0 else (-1, -1, -1)
            row += [*d, *s]
        lines.append(",".join(str(v) for v in row))
    log_path = os.path.join(tmp, "camera_spike.csv")
    with open(log_path, "w") as f:
        f.write("\n".join(lines) + "\n")

    # odx record started 3.2 s after the game's tick clock, QPC 7,000,000 ms at its first frame, 30 fps grid.
    capture = os.path.join(tmp, "capture")
    os.makedirs(capture)
    qpc0, rec_start_tick, rec_fps, rec_frames = 7_000_000.0, tick0 + 3200, 30, 1050
    frame_ticks = rec_start_tick + np.arange(rec_frames) * 1000 / rec_fps
    with open(os.path.join(capture, "video_frames.csv"), "w") as f:
        f.write("frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate\n")
        for k, t in enumerate(frame_ticks):
            q = qpc0 + (t - rec_start_tick)
            f.write(f"{k},{q},{q},0\n")
    lit = np.zeros(rec_frames, dtype=bool)
    for t in flash_ticks:  # the frame on screen at the flash tick (a 60 fps flash lasts 16.7 ms; the 30 fps grid samples it)
        k = int(np.searchsorted(frame_ticks, t))
        if k < rec_frames:
            lit[k] = True
    raw = np.zeros((rec_frames, 64, 64), dtype=np.uint8) + 20
    raw[lit, :CORNER_PX, :CORNER_PX] = 235
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gray", "-s", "64x64", "-r", str(rec_fps),
                    "-i", "-", "-c:v", "libx264", "-qp", "0", "-pix_fmt", "yuv420p", os.path.join(capture, "video.mp4")],
                   input=raw.tobytes(), check=True)

    result = {"layout": layout(load_log(log_path)), "clock": clock(load_log(log_path), capture)}
    lay, clk = result["layout"], result["clock"]
    expected_b = qpc0 - rec_start_tick
    checks = {
        "axes": lay["axes"] == {"right": "row0", "up": "row1", "forward": "row2"},
        "rms": lay["rms_px_with_logged_vfov"] < 0.01,
        "vfov": abs(lay["vfov_fitted_deg"] - vfov) < 0.01,
        "pairs": clk["pairs"] >= 5,
        "clock_rate": abs(clk["qpc_ms_per_tick_ms"] - 1) < 1e-3,
        "clock_offset_within_a_frame": abs(clk["qpc_at_tick_0_ms"] - expected_b) <= 1000 / rec_fps,
    }
    print(json.dumps({**result, "checks": checks}, indent=2))
    return 0 if all(checks.values()) else 1


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log", nargs="?")
    ap.add_argument("--capture", help="odx record folder (video.mp4 + video_frames.csv) recorded during the log")
    ap.add_argument("--corner", type=int, nargs=2, default=(0, 0), metavar=("X", "Y"),
                    help="top-left of the flash square in the video (a windowed capture includes the title bar)")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()
    if args.selftest:
        return selftest()
    if not args.log:
        ap.error("camera_spike.csv is required")
    log = load_log(args.log)
    result = {"layout": layout(log)}
    if args.capture:
        result["clock"] = clock(log, args.capture, args.corner)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
