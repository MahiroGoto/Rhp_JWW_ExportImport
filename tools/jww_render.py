# -*- coding: utf-8 -*-
# Draw a JWW file to PNG to check it visually (needs matplotlib).
# Usage: python jww_render.py in.jww out.png      (orange lines = text base lines)
import sys, os, math, struct
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jww_read as J
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager
plt.rcParams["font.family"] = "MS Gothic"

path, out = sys.argv[1], sys.argv[2]
b = open(path, "rb").read(); r = J.R(b); o = {}
ver = J.header(r, o)
pens = {}
n = r.cnt(); classes, idx = {}, [1]
ents = []
for k in range(n):
    tag = r.w()
    if tag == 0xFFFF:
        r.w(); ln = r.w(); name = b[r.p:r.p+ln].decode(); r.p += ln; classes[idx[0]] = name; idx[0] += 1
    else:
        name = classes[tag & 0x7FFF]
    idx[0] += 1
    st, col, lay = J.base(r, ver)
    if name == "CDataSen": ents.append(("L", col, st, r.u("dddd")))
    elif name == "CDataEnko": ents.append(("A", col, st, r.u("dddddddI")))
    elif name == "CDataMoji":
        v = r.u("ddddIdddd"); f = r.st(); t = r.st(); ents.append(("T", col, st, (v, t)))
colors = {1: "k", 2: "k", 3: "k", 4: "r", 5: "b"}
widths = {1: 0.3, 2: 0.5, 3: 1.2, 4: 0.5, 5: 0.5}
fig = plt.figure(figsize=(16.54, 11.69), dpi=120)
ax = fig.add_axes([0, 0, 1, 1]); ax.set_xlim(-210, 210); ax.set_ylim(-148.5, 148.5); ax.set_aspect("equal"); ax.axis("off")
ax.add_patch(plt.Rectangle((-210, -148.5), 420, 297, fill=False, lw=0.3, ec="0.6"))
pt = 72.0 / 25.4 * 16.54 * 25.4 / 420 * 1.0   # paper mm -> points
for kind, col, st, v in ents:
    c = colors.get(col, "k"); lw = widths.get(col, 0.5)
    ls = "-." if st == 5 else "-"
    if kind == "L":
        ax.plot([v[0], v[2]], [v[1], v[3]], c, lw=lw, ls=ls)
    elif kind == "A":
        cx, cy, R, a0, sw = v[:5]
        ts = [a0 + sw * i / 48 for i in range(49)]
        ax.plot([cx + R * math.cos(t) for t in ts], [cy + R * math.sin(t) for t in ts], c, lw=lw)
    else:
        (x1, y1, x2, y2, shu, sx, sy, sp, ang), t = v
        ax.text(x1, y1, t, fontsize=sy * 72 / 25.4 * (16.54 * 25.4 / 420) / 1.0 * 1.0 * 0.95,
                rotation=ang, rotation_mode="anchor", ha="left", va="baseline", color=c)
        ax.plot([x1, x2], [y1, y2], color="#ffaa00", lw=0.2)
fig.savefig(out)

