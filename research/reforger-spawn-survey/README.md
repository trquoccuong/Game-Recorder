# reforger-spawn-survey

How [`capture/deploy/aws/reforger/spawn_points.txt`](../../capture/deploy/aws/reforger/spawn_points.txt) (20 extra spawn
points on Everon, read by `OdxCapture`'s `OdxSpawnPoints.c`) was made, to make it again for another count or map.
Task and results: [document/games/arma-reforger-spawn-points.md](../../document/games/arma-reforger-spawn-points.md) §4.3.

| Path | What it is |
|---|---|
| `addon/OdxSurvey` | Server-side script mod: when the game mode starts, samples the terrain every 100 m and writes the points that are ≥ 3 m above the ocean, with nothing above the ground (downward trace from 60 m: no building, tree, rock), a surface within ~22° of flat and ≤ 2 m of height change within 6 m, to `$profile:odx/survey.txt` (`x z y`) |
| `pick_spawns.py` | Keeps the main island (largest group of points linked within 450 m), then k-means (k-means++ seed, fixed random seed) and the point nearest each centre; writes `x z Odx NN` lines. Standard library only |
| `draw_map.ps1` | Draws the survey and the picked points to a PNG |
| `survey-everon-1.6.0.68.txt` | The survey of Combat Ops Everon on 1.6.0.68 (2919 points): `pick_spawns.py` on it gives the committed `spawn_points.txt` byte for byte, no server needed |
| `spawn-points-map.png` | The 20 points of 2026-10-26 (north up) |

## Run

On Windows with the 1.6.0.68 server build (`document/games/arma-reforger.md`, "Pinned server builds"):

```powershell
$run = 'D:\odx-dev\reforger-survey'
& D:\odx-dev\reforger-server-1.6.0.68\ArmaReforgerServer.exe -server '{DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf' `
    -profile "$run\profile" -logsDir "$run\logs" -addonsDir D:\project\client\research\reforger-spawn-survey\addon -addons OdxSurvey
# wait for "TBC_Survey: kept N of M" in the log (about 30 s after the world loads), then stop the server
python -I pick_spawns.py "$run\profile\profile\odx\survey.txt" spawn_points.txt 20
powershell -File draw_map.ps1 -Survey "$run\profile\profile\odx\survey.txt" -Points spawn_points.txt
```

The survey knows nothing about the scenario: a point among Combat Ops' objectives or enemy AI has to be moved by hand.
Enforce Script note: `out` is a keyword, not a variable name.
