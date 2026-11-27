# -*- coding: utf-8 -*-
"""
Minimal JWW (Jw_cad for Windows) writer, pure Python (stdlib only).

Writes lines (CDataSen), arcs / circles (CDataEnko), points (CDataTen) and texts (CDataMoji)
into one layer group, with layer names, pen colours, line types and text types defined in the header.
The binary layout follows LibreCAD's jwwlib (libraries/jwwlib/src/jwwdoc.cpp / jwwdoc.h):
MFC CArchive serialisation, little-endian, strings in Shift-JIS (cp932) with a BYTE length
(0xFF + WORD for >= 255 bytes).

Coordinates are PAPER mm (図寸) with the paper centre at (0, 0); the layer-group scale (e.g. 100)
gives the real size. Text sizes are paper mm too. (Checked on real Jw_cad files: a dimension line of
4.8 units in a 1:50 group carries the value 240.)

    w = JwwWriter(scale=100, paper=3, group_name=u"...")
    w.set_layer_name(0, u"外形線")
    w.line(x1, y1, x2, y2, layer=0, color=2, style=1)
    w.arc(cx, cy, r, start_rad, sweep_rad, layer=0, color=2)
    w.text(x, y, u"文字", size=2.5, angle_deg=0, layer=1, color=2)
    w.save(path)
"""
import struct

VERSION = 600          # Jw_cad 6.00 data (>= 420: SXF tables, >= 351: pen width in CData)

# COLORREF = 0x00BBGGRR
def rgb(r, g, b):
    return (b << 16) | (g << 8) | r


class JwwWriter(object):
    def __init__(self, scale=100.0, paper=3, group_name=u"", memo=u""):
        self.scale = float(scale)
        self.paper = paper            # 0=A0 1=A1 2=A2 3=A3 4=A4
        self.group_name = group_name
        self.memo = memo
        self.layer_names = [u""] * 16
        self.lines, self.arcs, self.points, self.texts = [], [], [], []
        # pen colours 1..9: (screen RGB, screen width dots, print RGB, print width 1/100 mm)
        self.pens = {
            1: (rgb(0, 0, 0), 1, rgb(0, 0, 0), 13),      # thin black
            2: (rgb(0, 0, 0), 1, rgb(0, 0, 0), 18),      # black
            3: (rgb(0, 0, 0), 2, rgb(0, 0, 0), 35),      # thick black
            4: (rgb(255, 0, 0), 1, rgb(255, 0, 0), 18),  # red
            5: (rgb(0, 0, 255), 1, rgb(0, 0, 255), 18),  # blue
            6: (rgb(0, 160, 0), 1, rgb(0, 160, 0), 18),  # green
            7: (rgb(255, 0, 255), 1, rgb(255, 0, 255), 18),
            8: (rgb(128, 128, 128), 1, rgb(128, 128, 128), 13),
            9: (rgb(0, 160, 160), 1, rgb(0, 160, 160), 13),
        }
        self.text_sizes = []          # distinct paper sizes -> text types 1..10

    # ------------------------------------------------------------ API
    def set_layer_name(self, i, name):
        self.layer_names[i] = name

    def line(self, x1, y1, x2, y2, layer=0, color=2, style=1):
        self.lines.append((layer, color, style, x1, y1, x2, y2))

    def arc(self, cx, cy, r, start, sweep, layer=0, color=2, style=1, full=False):
        self.arcs.append((layer, color, style, cx, cy, r, start, sweep, 1 if full else 0))

    def circle(self, cx, cy, r, layer=0, color=2, style=1):
        import math
        self.arc(cx, cy, r, 0.0, 2 * math.pi, layer, color, style, True)

    def point(self, x, y, layer=0, color=2):
        self.points.append((layer, color, x, y))

    @staticmethod
    def text_units(s):
        """Width in full-width character units (half-width = 0.5), as Jw_cad counts it."""
        return len(s.encode("cp932", "replace")) / 2.0

    def text(self, x, y, s, size, angle_deg=0.0, layer=0, color=2, font=u"ＭＳ ゴシック", spacing=0.0):
        """(x, y) = bottom-left of the string, size = paper mm (character width = height)."""
        import math
        size = round(float(size), 3)
        if size not in self.text_sizes:
            self.text_sizes.append(size)
        w = self.text_units(s) * size + spacing * max(len(s) - 1, 0)
        a = math.radians(angle_deg)
        self.texts.append((layer, color, x, y, x + w * math.cos(a), y + w * math.sin(a),
                           size, spacing, angle_deg, font, s))

    # ------------------------------------------------------------ binary helpers
    def _w(self, fmt, *v):
        self.buf.append(struct.pack("<" + fmt, *v))

    def _str(self, s):
        b = s.encode("cp932", "replace") if s else b""
        n = len(b)
        if n == 0:
            self._w("B", 0)
            return
        if n >= 0xFF:
            self._w("BH", 0xFF, n)
        else:
            self._w("B", n)
        self.buf.append(b)

    # ------------------------------------------------------------ header
    def _header(self):
        w, d, s = (lambda v: self._w("I", v)), (lambda v: self._w("d", v)), self._str
        self.buf.append(b"JwwData.")
        w(VERSION)
        s(self.memo)
        w(self.paper)                      # 図面サイズ
        w(0)                               # 書込みレイヤグループ
        for g in range(16):
            w(2 if g == 0 else 1)          # group state: 2 = editable (0 = hidden, 1 = display only)
            w(0)                           # write layer
            d(self.scale)                  # scale denominator
            w(0)                           # protect
            for l in range(16):
                w(2 if g == 0 else 1)      # layer state
                w(0)                       # protect
        for i in range(14):
            w(0)                           # dummy
        for v in (0, 0, 0, 0, 0):
            w(v)                           # 寸法関係の設定
        w(0)                               # dummy
        w(100)                             # 線描画の最大幅
        d(0.0); d(0.0)                     # プリンタ出力範囲の原点
        d(1.0)                             # プリンタ出力倍率
        w(0)                               # プリンタ90°回転
        w(0)                               # 目盛設定モード
        d(0.0); d(0.0); d(0.0); d(0.0); d(0.0)   # 目盛
        for g in range(16):
            for l in range(16):
                s(self.layer_names[l] if g == 0 else u"")
        for g in range(16):
            s(self.group_name if g == 0 else u"")
        d(0.0); d(35.0); w(0); d(0.0)      # 日影
        d(0.0); d(0.0)                     # 天空図 (>= 300)
        w(1)                               # 2.5D mm 単位
        d(1.0); d(0.0); d(0.0)             # 画面倍率, 原点
        d(1.0); d(0.0); d(0.0)             # 範囲記憶
        for i in range(8):                 # マークジャンプ (>= 300)
            d(1.0); d(0.0); d(0.0); w(0)
        d(0.0); d(0.0); d(0.0); w(0); d(0.0); d(0.0); d(0.0); w(0)   # 文字描画状態 (>= 300)
        for i in range(10):
            d(0.0)                         # 複線間隔
        d(0.0)                             # 両側複線の留線出
        for i in range(10):                # 画面表示色・線幅 (0..9)
            p = self.pens.get(i, (rgb(0, 0, 0), 1, rgb(0, 0, 0), 13))
            w(p[0]); w(p[1])
        for i in range(10):                # プリンタ出力色・線幅・実点半径
            p = self.pens.get(i, (rgb(0, 0, 0), 1, rgb(0, 0, 0), 13))
            w(p[2]); w(p[3]); d(0.3)
        patterns = {2: 0xCCCCCCCC, 3: 0xF0F0F0F0, 4: 0xFF00FF00, 5: 0xFFFF1818,
                    6: 0xFFF0F0FF, 7: 0xFFFF1B1B, 8: 0xFFF6F6FF, 9: 0x88888888}
        for i in range(2, 10):             # 線種 2..9
            w(patterns[i]); w(32); w(1); w(1)
        for i in range(11, 16):            # ランダム線
            w(0); w(0); w(0); w(0); w(0)
        for i in range(16, 20):            # 倍長線種
            w(0xFFFF1818); w(64); w(1); w(1)
        for v in (0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0):   # 実点.. 印刷関係 (カラー印刷 = 1)
            w(v)
        w(0)                               # 作図時間
        w(0); w(0); w(0); w(0)             # 2.5D
        d(0.0); d(0.0); d(0.0); d(0.0); d(0.0)
        d(0.0); d(0.0); d(0.0); d(0.0)     # 最終値
        w(0); w(0)                         # ソリッド任意色
        for n in range(257):               # SXF 拡張線色 (>= 420)
            w(0); w(1)
        for n in range(257):
            s(u""); w(0); w(1); d(0.3)
        for n in range(33):                # SXF 拡張線種
            w(0); w(32); w(1); w(1)
        for n in range(33):
            s(u""); w(0)
            for j in range(10):
                d(0.0)
        sizes = (self.text_sizes + [2.5] * 10)[:10]
        for i in range(10):                # 文字種 1..10
            d(sizes[i]); d(sizes[i]); d(0.0); w(2)
        d(2.5); d(2.5); d(0.0); w(2); w(1) # 書込み文字
        d(0.0); d(0.0)                     # 文字位置整理
        w(0)                               # 文字基準点のずれ
        for i in range(6):
            d(0.0)

    # ------------------------------------------------------------ objects
    def _base(self, layer, color, style):
        self._w("I", 0)                    # 曲線属性番号
        self._w("B", style)                # 線種
        self._w("H", color)                # 線色
        self._w("H", 0)                    # 線色幅 (>= 351)
        self._w("H", layer)
        self._w("H", 0)                    # layer group
        self._w("H", 0)                    # flags

    def _tag(self, name):
        """MFC CArchive class tag: new class -> 0xFFFF, schema, name; known class -> index | 0x8000."""
        if name not in self.classes:
            self.classes[name] = self.index
            self.index += 1
            self._w("HHH", 0xFFFF, VERSION, len(name))
            self.buf.append(name.encode("ascii"))
        else:
            i = self.classes[name]
            if i < 0x7FFF:
                self._w("H", i | 0x8000)
            else:
                self._w("HI", 0x7FFF, i | 0x80000000)
        self.index += 1                    # the object itself

    def tobytes(self):
        self.buf, self.classes, self.index = [], {}, 1
        self._header()
        n = len(self.lines) + len(self.arcs) + len(self.points) + len(self.texts)
        if n < 0xFFFF:
            self._w("H", n)
        else:
            self._w("HI", 0xFFFF, n)
        for (layer, color, style, x1, y1, x2, y2) in self.lines:
            self._tag("CDataSen"); self._base(layer, color, style)
            self._w("dddd", x1, y1, x2, y2)
        for (layer, color, style, cx, cy, r, a0, sw, full) in self.arcs:
            self._tag("CDataEnko"); self._base(layer, color, style)
            self._w("dddddddI", cx, cy, r, a0, sw, 0.0, 1.0, full)
        for (layer, color, x, y) in self.points:
            self._tag("CDataTen"); self._base(layer, color, 1)
            self._w("ddI", x, y, 0)
        for (layer, color, x1, y1, x2, y2, size, sp, ang, font, s) in self.texts:
            self._tag("CDataMoji"); self._base(layer, color, 1)
            k = self.text_sizes.index(size) + 1 if size in self.text_sizes[:10] else 0
            self._w("ddddI", x1, y1, x2, y2, k)
            self._w("dddd", size, size, sp, ang)
            self._str(font)
            self._str(s)
        self._w("H", 0)                    # block definitions: none
        return b"".join(self.buf)

    def save(self, path):
        data = self.tobytes()
        with open(path, "wb") as fh:
            fh.write(data)
        return len(data)
