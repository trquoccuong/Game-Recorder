"""20 evenly spread points from survey.txt (x z y per line): k-means (k-means++ init, fixed seed), then the candidate
nearest each centroid. Usage: pick_spawns.py <survey.txt> <out spawn_points.txt> [k]"""
import math, random, sys

pts = [tuple(map(float, l.split()[:2])) for l in open(sys.argv[1]) if l.strip()]
k = int(sys.argv[3]) if len(sys.argv) > 3 else 20
rnd = random.Random(7)

def d2(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2

# Main island only: the largest group of candidates linked within 450 m (gaps of a few rejected grid cells).
cell = {(round(p[0] / 100), round(p[1] / 100)): p for p in pts}
seen, best = set(), []
for start in cell:
    if start in seen:
        continue
    comp, stack = [], [start]
    seen.add(start)
    while stack:
        c = stack.pop()
        comp.append(cell[c])
        for dx in range(-4, 5):
            for dz in range(-4, 5):
                n = (c[0] + dx, c[1] + dz)
                if n in cell and n not in seen:
                    seen.add(n)
                    stack.append(n)
    if len(comp) > len(best):
        best = comp
print(f'main island: {len(best)} of {len(pts)} candidates')
pts = best

cent = [rnd.choice(pts)]
while len(cent) < k:
    w = [min(d2(p, c) for c in cent) for p in pts]
    r = rnd.random() * sum(w)
    for p, wi in zip(pts, w):
        r -= wi
        if r <= 0:
            cent.append(p)
            break
for _ in range(100):
    groups = [[] for _ in cent]
    for p in pts:
        groups[min(range(k), key=lambda i: d2(p, cent[i]))].append(p)
    new = [(sum(p[0] for p in g) / len(g), sum(p[1] for p in g) / len(g)) if g else c for g, c in zip(groups, cent)]
    if new == cent:
        break
    cent = new

picked = sorted((min(pts, key=lambda p: d2(p, c)) for c in cent), key=lambda p: (-p[1], p[0]))
with open(sys.argv[2], 'w', newline='\n') as f:
    f.write('# 20 points spread over Everon\'s main island: k-means over open, dry, gentle land sampled every 100 m (1.6.0.68)\n')
    for i, (x, z) in enumerate(picked, 1):
        f.write(f'{x:.0f} {z:.0f} Odx {i:02d}\n')
gaps = [min(math.sqrt(d2(a, b)) for b in picked if b is not a) for a in picked]
print(f'{len(pts)} candidates, {k} picked; nearest-neighbour distance min {min(gaps):.0f} m, max {max(gaps):.0f} m')
print(open(sys.argv[2]).read())
