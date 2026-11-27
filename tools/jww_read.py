# -*- coding: utf-8 -*-
# independent JWW reader (follows jwwlib Read logic) to validate the writer
import struct, sys, collections, io

class R:
    def __init__(s, b): s.b, s.p = b, 0
    def u(s, f):
        v = struct.unpack_from("<" + f, s.b, s.p); s.p += struct.calcsize("<" + f); return v if len(v) > 1 else v[0]
    def dw(s): return s.u("I")
    def w(s): return s.u("H")
    def d(s): return s.u("d")
    def by(s): return s.u("B")
    def st(s):
        n = s.by()
        if n == 0xFF:
            n = s.w()
            if n == 0xFFFF: n = s.dw()
        t = s.b[s.p:s.p + n]; s.p += n
        return t.decode("cp932", "replace")
    def cnt(s):
        n = s.w()
        if n == 0xFFFF: n = s.dw()
        return n

def header(r, out):
    assert r.b[:8] == b"JwwData."; r.p = 8
    ver = r.dw(); out["version"] = ver
    out["memo"] = r.st(); out["zumen"] = r.dw(); r.dw()
    scales = []
    for g in range(16):
        r.dw(); r.dw(); scales.append(r.d()); r.dw()
        for l in range(16): r.dw(); r.dw()
    out["scales"] = scales
    for i in range(14): r.dw()
    for i in range(5): r.dw()
    r.dw(); r.dw(); r.d(); r.d(); r.d(); r.dw(); r.dw()
    for i in range(5): r.d()
    names = [[r.st() for j in range(16)] for i in range(16)]
    out["lay0"] = [n for n in names[0] if n][:6]
    out["gname0"] = [r.st() for i in range(16)][0]
    r.d(); r.d(); r.dw(); r.d()
    if ver >= 300: r.d(); r.d()
    r.dw(); r.d(); r.d(); r.d(); r.d(); r.d(); r.d()
    if ver >= 300:
        for i in range(8): r.d(); r.d(); r.d(); r.dw()
    else:
        for i in range(4): r.d(); r.d(); r.d()
    if ver >= 300:
        r.d(); r.d(); r.d(); r.dw(); r.d(); r.d(); r.d(); r.dw()
    for i in range(11): r.d()
    for i in range(10): r.dw(); r.dw()
    for i in range(10): r.dw(); r.dw(); r.d()
    for i in range(8): r.dw(); r.dw(); r.dw(); r.dw()
    for i in range(5): r.dw(); r.dw(); r.dw(); r.dw(); r.dw()
    for i in range(4): r.dw(); r.dw(); r.dw(); r.dw()
    for i in range(11): r.dw()
    r.dw(); r.dw(); r.dw(); r.dw(); r.dw()
    for i in range(9): r.d()
    r.dw(); r.dw()
    if ver >= 420:
        for n in range(257): r.dw(); r.dw()
        for n in range(257): r.st(); r.dw(); r.dw(); r.d()
        for n in range(33): r.dw(); r.dw(); r.dw(); r.dw()
        for n in range(33):
            r.st(); r.dw()
            for j in range(10): r.d()
    sizes = []
    for i in range(10): sizes.append(r.d()); r.d(); r.d(); r.dw()
    out["textsizes"] = sizes
    r.d(); r.d(); r.d(); r.dw(); r.dw(); r.d(); r.d(); r.dw()
    for i in range(6): r.d()
    out["header_end"] = r.p
    return ver

LASTG = [0]
def base(r, ver):
    g = r.dw(); st = r.by(); col = r.w()
    if ver >= 351: r.w()
    lay = r.w(); gl = r.w(); fl = r.w()
    LASTG[0] = gl
    return st, col, lay
BB = {}
SUN = []

def objects(r, ver, out, n, classes, idx, depth=0):
    kinds = collections.Counter(); samples = []
    for k in range(n):
        tag = r.w()
        if tag == 0xFFFF:
            schema = r.w(); ln = r.w(); name = r.b[r.p:r.p + ln].decode(); r.p += ln
            classes[idx[0]] = name; out.setdefault("schemas", set()).add(schema); idx[0] += 1
        elif tag == 0x7FFF:
            name = classes[r.dw() & 0x7FFFFFFF]
        else:
            name = classes[tag & 0x7FFF]
        idx[0] += 1
        st, col, lay = base(r, ver)
        if name == "CDataSen":
            v = r.u("dddd"); g = LASTG[0]
            bb = BB.setdefault(g, [1e18, 1e18, -1e18, -1e18, 0])
            for x, y in ((v[0], v[1]), (v[2], v[3])):
                bb[0] = min(bb[0], x); bb[1] = min(bb[1], y); bb[2] = max(bb[2], x); bb[3] = max(bb[3], y)
            bb[4] += 1
        elif name == "CDataEnko": v = r.u("dddddddI")
        elif name == "CDataTen":
            v = r.u("ddI")
            if st == 100: r.u("Idd")
        elif name == "CDataMoji":
            v = r.u("ddddIdddd"); f = r.st(); t = r.st(); v = (v, f, t)
            if len(samples) < 6: samples.append((round(v[0][0]), round(v[0][1]), v[0][5], f, t))
        elif name == "CDataSolid":
            v = r.u("dddddddd")
            if col == 10: r.dw()
        elif name == "CDataBlock": v = r.u("dddddI")
        elif name == "CDataSunpou":
            base(r, ver); sv = r.u("dddd")               # sen
            base(r, ver); r.u("ddddIdddd"); r.st(); tv = r.st()   # moji
            SUN.append((round(((sv[2]-sv[0])**2 + (sv[3]-sv[1])**2) ** 0.5, 3), tv, LASTG[0]))
            if ver >= 420:
                r.w()
                for q in range(2): base(r, ver); r.u("dddd")
                for q in range(4):
                    s2, c2, l2 = base(r, ver); r.u("ddI")
                    if s2 == 100: r.u("Idd")
        else:
            raise Exception("unknown class %r at %d" % (name, r.p))
        kinds[name] += 1
    return kinds, samples

def read(path):
    b = open(path, "rb").read(); r = R(b); out = {}
    ver = header(r, out)
    n = r.cnt(); out["count"] = n
    classes, idx = {}, [1]
    kinds, samples = objects(r, ver, out, n, classes, idx)
    out["kinds"] = dict(kinds); out["text_samples"] = samples
    out["after_list_pos"] = r.p; out["file_size"] = len(b)
    out["next_bytes"] = b[r.p:r.p + 16].hex()
    out["line_bbox_by_group"] = {g: [round(x) for x in v[:4]] + [v[4]] for g, v in BB.items()}
    BB.clear()
    out["sunpou_len_text_group"] = SUN[:8]
    del SUN[:]
    return out

if __name__ == "__main__":
    for p in sys.argv[1:]:
        try:
            o = read(p)
        except Exception as e:
            o = {"error": repr(e)}
        with io.open(p + ".txt", "w", encoding="utf-8") as fh:
            for k, v in o.items():
                fh.write(u"%s %s\n" % (k, v))
