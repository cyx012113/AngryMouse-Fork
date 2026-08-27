#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成 AngryMouse 鼠标贴图套建（程序化绘制）
============================================

为 Xfce4 / DMZ / Nord 三种风格各绘制 14 种光标角色 PNG，并对每张图运行
智能焦点识别器（hotspot_detector.py）计算偏移，写入 cursor-settings.txt，
使套建自带的焦点位置即已最优化（无需用户手动调整）。

输出目录：
  <repo>/AngryMouse/Resources/CursorCollections/<Style>/Rendered/*.png
  <repo>/AngryMouse/Resources/CursorCollections/<Style>/cursor-settings.txt

依赖：numpy、Pillow。OpenCV 可选（检测器会自动降级）。
"""

import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

# 让脚本可 import HotspotDetector 目录下的 hotspot_detector
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "HotspotDetector"))
import hotspot_detector  # noqa: E402

# 角色键 -> 默认文件名（须与 AngryMouse Roles 数组一致）
ROLES = [
    "arrow", "ibeam", "wait", "appstarting", "crosshair", "uparrow",
    "sizens", "sizewe", "sizenwse", "sizenesw", "sizeall", "no", "hand", "help",
]

# AngryMouse 运行期将光标缩放到此高度再算基准热点
RUNTIME_TARGET_H = 254
NATIVE = 32          # 生成的原生尺寸（正方形）
SS = 8               # 超采样倍数，用于抗锯齿
OUT_W = OUT_H = NATIVE * SS


# ----------------------------------------------------------------------------
# 配色方案
# ----------------------------------------------------------------------------
SCHEMES = {
    # Xfce4 经典：黑填充 + 白描边
    "Xfce4": dict(fill=(0, 0, 0, 255), outline=(255, 255, 255, 255), lw=2.2, accent=None),
    # DMZ-White：白填充 + 黑描边
    "DMZ": dict(fill=(255, 255, 255, 255), outline=(0, 0, 0, 255), lw=2.2, accent=None),
    # Nord：深石板填充 + 浅灰描边
    "Nord": dict(fill=(46, 52, 64, 255), outline=(216, 222, 233, 255), lw=2.2,
                 accent=(136, 192, 208, 255)),
}


def _poly(draw, pts, fill, outline, lw):
    draw.polygon(pts, fill=fill)
    if outline is not None and lw > 0:
        draw.line(pts + [pts[0]], fill=outline, width=int(round(lw)))


def _line(draw, xy, color, width):
    draw.line(xy, fill=color, width=int(round(width)))


def _ellipse(draw, box, fill=None, outline=None, width=1):
    draw.ellipse(box, fill=fill, outline=outline, width=int(round(width)))


def _arrow_polygon(cx=0, cy=0, s=1.0):
    """标准左上箭头（尖端在左上），相对坐标。"""
    base = [
        (1, 1), (1, 13), (5, 9), (9, 15), (11, 14),
        (7, 8), (13, 8),
    ]
    return [(x * s + cx, y * s + cy) for (x, y) in base]


def draw_role(role, draw, scheme):
    f = scheme["fill"]
    o = scheme["outline"]
    a = scheme["accent"]
    lw = scheme["lw"] * SS
    c = OUT_W / 2.0        # 画布中心
    bs = float(SS)         # 基础缩放（角色相对 32 网格）
    s = bs

    if role == "arrow":
        _poly(draw, _arrow_polygon(s=s), f, o, lw)

    elif role == "ibeam":
        # 竖直 I 形，含上下衬线
        x0 = c - 1.2 * s
        x1 = c + 1.2 * s
        _line(draw, [(c, 2 * s), (c, 30 * s)], f, 2.4 * s)
        _line(draw, [(x0, 3 * s), (x1, 3 * s)], f, 2.4 * s)
        _line(draw, [(x0, 29 * s), (x1, 29 * s)], f, 2.4 * s)
        if o is not None:
            _line(draw, [(c, 2 * s), (c, 30 * s)], o, 1 * s)
            _line(draw, [(x0, 3 * s), (x1, 3 * s)], o, 1 * s)
            _line(draw, [(x0, 29 * s), (x1, 29 * s)], o, 1 * s)

    elif role == "wait":
        # 圆环 + 顶部指针
        r = 8 * s
        _ellipse(draw, [c - r, c - r, c + r, c + r], fill=None, outline=f, width=2.4 * s)
        _line(draw, [(c, c), (c, c - r)], f, 2.4 * s)
        if o is not None:
            _ellipse(draw, [c - r, c - r, c + r, c + r], fill=None, outline=o, width=1 * s)

    elif role == "appstarting":
        r = 7 * s
        _ellipse(draw, [c - r, c - r, c + r, c + r], fill=None, outline=f, width=2.4 * s)
        # 右上小箭头
        _poly(draw, _arrow_polygon(cx=c + 2 * s, cy=c - 10 * s, s=s * 0.8), f, o, lw)

    elif role == "crosshair":
        # 带中心缺口的十字
        gap = 3 * s
        _line(draw, [(c, 1 * s), (c, c - gap)], f, 2.0 * s)
        _line(draw, [(c, c + gap), (c, 31 * s)], f, 2.0 * s)
        _line(draw, [(1 * s, c), (c - gap, c)], f, 2.0 * s)
        _line(draw, [(c + gap, c), (31 * s, c)], f, 2.0 * s)
        if o is not None:
            _ellipse(draw, [c - 1.5 * s, c - 1.5 * s, c + 1.5 * s, c + 1.5 * s], outline=o, width=1 * s)

    elif role == "uparrow":
        # 向上箭头（尖端朝上）
        pts = [
            (c, 2 * s), (c + 6 * s, 14 * s), (c + 2.5 * s, 14 * s),
            (c + 2.5 * s, 30 * s), (c - 2.5 * s, 30 * s), (c - 2.5 * s, 14 * s),
            (c - 6 * s, 14 * s),
        ]
        _poly(draw, pts, f, o, lw)

    elif role == "sizens":
        _double_arrow(draw, f, o, lw, s, c, vertical=True)

    elif role == "sizewe":
        _double_arrow(draw, f, o, lw, s, c, vertical=False)

    elif role == "sizenwse":
        _double_arrow_diag(draw, f, o, lw, s, c, flip=False)

    elif role == "sizenesw":
        _double_arrow_diag(draw, f, o, lw, s, c, flip=True)

    elif role == "sizeall":
        _move_all(draw, f, o, lw, s, c)

    elif role == "no":
        # 禁止符号：圆环 + 斜杠
        r = 9 * s
        _ellipse(draw, [c - r, c - r, c + r, c + r], fill=None, outline=f, width=2.4 * s)
        _line(draw, [(c - r * 0.7, c - r * 0.7), (c + r * 0.7, c + r * 0.7)], f, 2.6 * s)
        if o is not None:
            _ellipse(draw, [c - r, c - r, c + r, c + r], fill=None, outline=o, width=1 * s)

    elif role == "hand":
        _hand(draw, f, o, lw, s, c, a)

    elif role == "help":
        # 箭头 + 问号气泡
        _poly(draw, _arrow_polygon(s=s), f, o, lw)
        r = 5 * s
        bcx, bcy = c + 9 * s, c + 11 * s
        _ellipse(draw, [bcx - r, bcy - r, bcx + r, bcy + r], fill=f, outline=o, width=1 * s)
        # 问号简化：上弧 + 竖 + 点
        _line(draw, [(bcx - 2 * s, bcy - 1 * s), (bcx + 2 * s, bcy - 1 * s)], o, 1.2 * s)
        _line(draw, [(bcx, bcy - 2.5 * s), (bcx, bcy - 1 * s)], o, 1.2 * s)
        _ellipse(draw, [bcx - 0.8 * s, bcy + 2 * s, bcx + 0.8 * s, bcy + 2.8 * s], fill=o, width=1 * s)

    else:
        raise ValueError("unknown role: " + role)


def _double_arrow(draw, f, o, lw, s, c, vertical):
    if vertical:
        # 上箭头
        _poly(draw, [(c, 2 * s), (c + 4 * s, 9 * s), (c - 4 * s, 9 * s)], f, o, lw)
        # 下箭头
        _poly(draw, [(c, 30 * s), (c + 4 * s, 23 * s), (c - 4 * s, 23 * s)], f, o, lw)
        _line(draw, [(c, 9 * s), (c, 23 * s)], f, 2.0 * s)
    else:
        _poly(draw, [(2 * s, c), (9 * s, c - 4 * s), (9 * s, c + 4 * s)], f, o, lw)
        _poly(draw, [(30 * s, c), (23 * s, c - 4 * s), (23 * s, c + 4 * s)], f, o, lw)
        _line(draw, [(9 * s, c), (23 * s, c)], f, 2.0 * s)


def _double_arrow_diag(draw, f, o, lw, s, c, flip):
    # NW-SE 或 NE-SW 对角线双向箭头
    if flip:
        # NE-SW
        tip1 = (c + 9 * s, c - 9 * s)
        tip2 = (c - 9 * s, c + 9 * s)
    else:
        # NW-SE
        tip1 = (c - 9 * s, c - 9 * s)
        tip2 = (c + 9 * s, c + 9 * s)
    d = 4 * s
    perpx = 1 if flip else -1
    p1 = _tri(tip1, c, s, d, perpx, 1)
    p2 = _tri(tip2, c, s, d, -perpx, -1)
    _poly(draw, p1, f, o, lw)
    _poly(draw, p2, f, o, lw)
    _line(draw, [tip1, tip2], f, 2.0 * s)


def _tri(tip, c, s, d, px, py):
    tx, ty = tip
    return [(tx, ty),
            (tx - px * d, ty + py * d * 0.6),
            (tx - px * d * 0.6, ty + py * d)]


def _move_all(draw, f, o, lw, s, c):
    L = 7 * s
    # 上下左右四箭头
    _poly(draw, [(c, 2 * s), (c + 4 * s, 10 * s), (c - 4 * s, 10 * s)], f, o, lw)
    _poly(draw, [(c, 30 * s), (c + 4 * s, 22 * s), (c - 4 * s, 22 * s)], f, o, lw)
    _poly(draw, [(2 * s, c), (10 * s, c - 4 * s), (10 * s, c + 4 * s)], f, o, lw)
    _poly(draw, [(30 * s, c), (22 * s, c - 4 * s), (22 * s, c + 4 * s)], f, o, lw)
    _line(draw, [(c, 10 * s), (c, 22 * s)], f, 1.8 * s)
    _line(draw, [(10 * s, c), (22 * s, c)], f, 1.8 * s)


def _hand(draw, f, o, lw, s, c, a):
    # 简化手掌指针：掌心圆角矩形 + 四指 + 拇指
    # 掌心
    _round_rect(draw, [c - 7 * s, c - 2 * s, c + 7 * s, c + 11 * s], f, o, lw)
    # 四根手指（竖直圆角条）
    for fx in (-5, -1.5, 2, 5.5):
        _round_rect(draw, [c + fx * s, c - 9 * s, c + (fx + 2.6) * s, c + 2 * s], f, o, lw * 0.6)
    # 拇指
    _round_rect(draw, [c - 11 * s, c + 1 * s, c - 6 * s, c + 6 * s], f, o, lw * 0.6)
    if a is not None:
        # 指尖小高亮点（仅装饰）
        _ellipse(draw, [c - 4.4 * s, c - 8 * s, c - 2.4 * s, c - 6 * s], fill=a, width=1 * s)


def _round_rect(draw, box, fill, outline, width):
    draw.rounded_rectangle(box, radius=min(box[2] - box[0], box[3] - box[1]) * 0.4,
                           fill=fill, outline=outline, width=int(round(width)))


# ----------------------------------------------------------------------------
# AngryMouse 基准热点复现（与 C# CalculatePngHotspot 一致的 254 空间算法）
# ----------------------------------------------------------------------------
def angrys_base(role, png_path, target_h=RUNTIME_TARGET_H):
    img = Image.open(png_path).convert("RGBA")
    w, h = img.size
    scale = target_h / float(h)
    tw = max(1, int(round(w * scale)))
    scaled = img.resize((tw, target_h), Image.BILINEAR)
    arr = np.array(scaled)
    mask = arr[:, :, 3] >= 128
    ys, xs = np.where(mask)
    if xs.size == 0:
        return (tw / 2.0, target_h / 2.0)

    minx, maxx = int(xs.min()), int(xs.max())
    miny, maxy = int(ys.min()), int(ys.max())

    # 第一个不透明像素（左上扫描）
    first = None
    for y in range(target_h):
        row = xs[ys == y]
        if row.size:
            first = (int(row[0]), y)
            break
    # 最上行
    top_y = None
    top_x0 = top_x1 = None
    for y in range(target_h):
        row = xs[ys == y]
        if row.size:
            top_x0, top_x1 = int(row[0]), int(row[-1])
            top_y = y
            break

    role = (role or "").lower()
    if role in ("arrow", "appstarting"):
        return first
    if role == "uparrow":
        return ((top_x0 + top_x1) / 2.0, float(top_y))
    return ((minx + maxx) / 2.0, (miny + maxy) / 2.0)


def analytic_hotspot(role):
    """返回角色在 32 网格原生坐标系下的真实热点 (x, y)。

    这些光标由本脚本程序化绘制，几何已知，热点按 X11 / Windows 约定给出：
      arrow/appstarting/help -> 左上箭头尖端
      uparrow                -> 最上端
      hand                  -> 中指指尖（顶部居中）
      其余（ibeam/wait/crosshair/sens/sizewe/sizenwse/sizenesw/sizeall/no）
                            -> 包围盒中心
    """
    c = 16.0  # 32 网格中心
    if role in ("arrow", "help"):
        return (1.0, 1.0)
    if role == "appstarting":
        # 右上小箭头：_arrow_polygon(cx=c+2, cy=c-10, s=0.8)，尖端 (1,1)
        return (1.0 * 0.8 + (c + 2.0), 1.0 * 0.8 + (c - 10.0))
    if role == "uparrow":
        return (c, 2.0)
    if role == "hand":
        # 中指圆角条 [c-1.5, c-9, c+1.1, c+2]，指尖在顶部居中
        return (c - 0.2, c - 9.0)
    return (c, c)


def main():
    repo = os.path.dirname(os.path.dirname(HERE))  # Tools/.. -> repo
    out_root = os.path.join(repo, "AngryMouse", "Resources", "CursorCollections")

    summary = []
    for style, scheme in SCHEMES.items():
        rendered = os.path.join(out_root, style, "Rendered")
        os.makedirs(rendered, exist_ok=True)

        offsets = {}
        for role in ROLES:
            img = Image.new("RGBA", (OUT_W, OUT_H), (0, 0, 0, 0))
            draw = ImageDraw.Draw(img)
            draw_role(role, draw, scheme)
            # 缩小到原生尺寸并抗锯齿（最终分发尺寸）
            native = img.resize((NATIVE, NATIVE), Image.LANCZOS)
            png_path = os.path.join(rendered, role + ".png")
            native.save(png_path)

            # 分发包热点：解析计算（几何已知，保证正确），与智能检测器交叉验证。
            native_hs = analytic_hotspot(role)
            true254 = (native_hs[0] * RUNTIME_TARGET_H / float(NATIVE),
                       native_hs[1] * RUNTIME_TARGET_H / float(NATIVE))
            base = angrys_base(role, png_path)   # 复现 C# CalculatePngHotspot 的 254 空间基线
            off_x = round(true254[0] - base[0])
            off_y = round(true254[1] - base[1])

            # 交叉验证：在高分辨率原图上跑智能检测器，仅作记录（不影响分发包结果）
            det = hotspot_detector.detect_pil(img, target_height=RUNTIME_TARGET_H)
            offsets[role] = (off_x, off_y)
            summary.append((style, role, det["method"], det["confidence"],
                            native_hs, (off_x, off_y)))

        # 写 cursor-settings.txt
        lines = ["# AngryMouse cursor render settings",
                 "# Generated by Tools/CursorGen/generate_collections.py (AI smart hotspot)"]
        for role in ROLES:
            ox, oy = offsets[role]
            lines.append("%s.hotspotOffsetX=%d" % (role, ox))
            lines.append("%s.hotspotOffsetY=%d" % (role, oy))
        settings_path = os.path.join(out_root, style, "cursor-settings.txt")
        with open(settings_path, "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
        print("Wrote %s (%d roles)" % (settings_path, len(ROLES)))

    print("\n--- summary (detector method / confidence / analytic hotspot / shipped offset) ---")
    for style, role, method, conf, hs, off in summary:
        print("%-7s %-11s %-9s conf=%.2f hs=(%.2f,%.2f) off=(%d,%d)" %
              (style, role, method, conf, hs[0], hs[1], off[0], off[1]))


if __name__ == "__main__":
    main()
