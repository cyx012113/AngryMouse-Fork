#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AngryMouse 智能鼠标贴图焦点（hotspot）识别器
================================================

通过 OpenCV / numpy 分析光标 PNG（含 alpha 通道），推断最合理的点击焦点位置。

策略：
  1. 读取 alpha 通道得到不透明掩码；若 PNG 无 alpha，则用亮度阈值近似。
  2. 若给定 --role 且该角色在标准光标约定表（ROLE_CONVENTIONS）中存在，则直接采用
     约定热点（归一化坐标）。1px 描边多边形光标（手/对角缩放）的纯几何法无法区分“指尖”
     与“中心”，约定表在此作为权威来源，保证正确率。
  3. 未知角色时退化为几何法：计算质心 (centroid)，对掩码沿 X / Y 轴做镜像求 IoU 判定
     对称性；对称形状（十字、圆圈、工字、双头/对角缩放）取质心；非对称形状（箭头、手）
     取凸包最薄顶点（尖端）+ 左上优先作为焦点候选。
  4. 局部质心精修：对几何法候选点周围小半径内的不透明像素按 alpha 加权求质心，
     得到亚像素稳定的焦点（约定表模式下跳过，直接使用精确约定点）。

输出 JSON（写到 stdout）：
  {
    "width":  int,                       # 原图像素宽
    "height": int,                       # 原图像素高
    "x":      float,                     # 焦点像素 X（左上原点）
    "y":      float,                     # 焦点像素 Y
    "nx":     float,                     # 归一化焦点 X [0,1]
    "ny":     float,                     # 归一化焦点 Y [0,1]
    "sx":     float|null,                # 若给了 --target-height，则按该高度缩放后的 X
    "sy":     float|null,                # 同上 Y
    "method": "tip" | "centroid" | "empty" | "fallback",
    "confidence": float [0,1],
    "shape":  "arrow"|"hand"|"crosshair"|"ibeam"|"symmetric"|"unknown",
    "symmetry_x": float,                 # X 轴镜像 IoU
    "symmetry_y": float                  # Y 轴镜像 IoU
  }

退出码：0 成功；非 0 失败（此时 C# 侧应回退到内置启发式）。

依赖：numpy、Pillow；OpenCV(cv2) 可选（仅用于更稳健的轮廓/距离变换，缺失时自动降级）。
"""

import argparse
import json
import math
import os
import sys

import numpy as np
from PIL import Image

# OpenCV 用于凸包/轮廓分析（找最尖锐顶点）。缺失时自动降级到“距质心最远”启发式。
try:
    import cv2
    _HAS_CV2 = True
except Exception:  # noqa: BLE001
    _HAS_CV2 = False


ALPHA_THRESHOLD = 128          # 视为“不透明”的 alpha 下限
SYMMETRY_IOS = 0.82            # 双轴镜像 IoU 均高于此值视为“对称形状”
REFINE_RADIUS = 2              # 局部质心精修半径（像素）
MIN_FOREGROUND_RATIO = 1e-4    # 前景占比下限，过低视为空图

# 角色约定热点（归一化 [0,1]，左上原点，width/height 归一化）。
# 值由生成器 generate_collections.analytic_hotspot(role)（几何已知真值）按 /32 推导，
# 与 C# CalculatePngHotspot 的 254 空间算法（true254 = hs * 254 / 32）完全一致，
# 从而保证“自动识别焦点”按钮在套建自带光标上得到的偏移≈0，且对遵循 X11/Windows
# 约定的通用光标同样合理：
#   - 指向型（箭头/手/帮助/铅笔）：热点在活动字形的尖端
#       · arrow 尖端在左上角 (1,1)  → 约 (0.031, 0.031)
#       · hand 中指指尖在顶部居中   → 约 (0.494, 0.219)
#       · appstarting 右上小箭头尖端 → 约 (0.588, 0.213)
#       · uparrow 最上端居中        → (0.5, 0.0625)
#   - 对称/调整大小/禁止/文本：热点在包围盒中心 → (0.5, 0.5)
# 分辨率无关：缩放任意 target_height 时，热点像素 = nx * 画布宽。
ROLE_CONVENTIONS = {
    # 箭头系：尖端在左上
    "arrow": (0.03125, 0.03125),
    "default": (0.03125, 0.03125),
    "top_left_arrow": (0.03125, 0.03125),
    "pointer": (0.03125, 0.03125),
    "link": (0.03125, 0.03125),
    "alias": (0.03125, 0.03125),
    "copy": (0.03125, 0.03125),
    "dnd": (0.03125, 0.03125),
    "url": (0.03125, 0.03125),
    "pencil": (0.03125, 0.03125),
    "help": (0.03125, 0.03125),
    "question_arrow": (0.03125, 0.03125),
    "context-menu": (0.03125, 0.03125),
    # 手系：中指指尖在顶部居中偏左
    "hand": (0.49375, 0.21875),
    "hand1": (0.49375, 0.21875),
    "hand2": (0.49375, 0.21875),
    "handwriting": (0.49375, 0.21875),
    "pointing_hand": (0.49375, 0.21875),
    # 等待/进度：右上小箭头尖端
    "appstarting": (0.5875, 0.2125),
    # 向上箭头：最上端居中
    "uparrow": (0.5, 0.0625),
    # 对称系：中心
    "ibeam": (0.5, 0.5),
    "text": (0.5, 0.5),
    "xterm": (0.5, 0.5),
    "wait": (0.5, 0.5),
    "watch": (0.5, 0.5),
    "busy": (0.5, 0.5),
    "crosshair": (0.5, 0.5),
    "cross": (0.5, 0.5),
    "diamond_cross": (0.5, 0.5),
    "sizens": (0.5, 0.5),
    "size_ns": (0.5, 0.5),
    "row_resize": (0.5, 0.5),
    "sizewe": (0.5, 0.5),
    "size_ew": (0.5, 0.5),
    "col_resize": (0.5, 0.5),
    "sizenwse": (0.5, 0.5),
    "nwse-resize": (0.5, 0.5),
    "sizenesw": (0.5, 0.5),
    "nesw-resize": (0.5, 0.5),
    "sizeall": (0.5, 0.5),
    "move": (0.5, 0.5),
    "fleur": (0.5, 0.5),
    "no": (0.5, 0.5),
    "not-allowed": (0.5, 0.5),
    "forbidden": (0.5, 0.5),
    "circle": (0.5, 0.5),
    "cell": (0.5, 0.5),
}


def load_mask(path):
    """返回 (rgb_array, mask_bool, (width, height))。mask 为不透明像素。"""
    img = Image.open(path).convert("RGBA")
    arr = np.asarray(img)
    height, width = arr.shape[:2]

    alpha = arr[:, :, 3].astype(np.float32)
    mask = alpha >= ALPHA_THRESHOLD

    # 没有有效 alpha 信息时（例如不透明 RGB），用亮度阈值近似。
    if mask.sum() == 0:
        gray = arr[:, :, :3].astype(np.float32).mean(axis=2)
        mask = gray >= 8

    return arr, mask, (width, height)


def symmetry_iou(mask, axis):
    """计算掩码沿 axis('x' 或 'y') 镜像后与自身的 IoU。"""
    if axis == "x":
        flipped = np.flip(mask, axis=1)
    else:
        flipped = np.flip(mask, axis=0)

    inter = np.logical_and(mask, flipped).sum()
    union = np.logical_or(mask, flipped).sum()
    if union == 0:
        return 1.0
    return float(inter) / float(union)


def detect(path, target_height=None, role=None):
    arr, mask, (width, height) = load_mask(path)
    return _analyze(arr, mask, width, height, target_height, role)


def detect_pil(image, target_height=None, role=None):
    """直接对内存中的 PIL Image 做检测（避免临时文件，适合高分辨率原图）。"""
    arr = np.asarray(image.convert("RGBA"))
    height, width = arr.shape[:2]
    alpha = arr[:, :, 3].astype(np.float32)
    mask = alpha >= ALPHA_THRESHOLD
    if mask.sum() == 0:
        gray = arr[:, :, :3].astype(np.float32).mean(axis=2)
        mask = gray >= 8
    return _analyze(arr, mask, width, height, target_height, role)


def _role_convention(role):
    """返回角色约定热点 (nx, ny) 或 None。匹配：精确小写名，否则按关键字子串。"""
    if not role:
        return None
    key = (role or "").lower().strip()
    if key in ROLE_CONVENTIONS:
        return ROLE_CONVENTIONS[key]
    # 子串/别名匹配（例如 "left_ptr" 含 "arrow"，"ns-resize" 含 "resize"）
    for alias, value in ROLE_CONVENTIONS.items():
        if alias in key or key in alias:
            return value
    return None


def _analyze(arr, mask, width, height, target_height=None, role=None):
    alpha = arr[:, :, 3].astype(np.float32)

    ys, xs = np.where(mask)
    n = xs.size

    result_base = {
        "width": int(width),
        "height": int(height),
        "x": float(width) / 2.0,
        "y": float(height) / 2.0,
        "nx": 0.5,
        "ny": 0.5,
        "sx": None,
        "sy": None,
        "method": "fallback",
        "confidence": 0.0,
        "shape": "unknown",
        "symmetry_x": 0.0,
        "symmetry_y": 0.0,
    }

    if n == 0 or (n / float(width * height)) < MIN_FOREGROUND_RATIO:
        result_base["method"] = "empty"
        return result_base

    # 约定表优先：已知角色直接采用标准热点，避免 1px 描边光标的几何歧义。
    conv = _role_convention(role)
    if conv is not None:
        nx, ny = float(conv[0]), float(conv[1])
        px = nx * width
        py = ny * height
        out = dict(result_base)
        out.update({
            "x": px,
            "y": py,
            "nx": nx,
            "ny": ny,
            "method": "role",
            "confidence": 0.95,
            "shape": "role-convention",
            "symmetry_x": 1.0,
            "symmetry_y": 1.0,
        })
        if target_height and height > 0:
            scale = float(target_height) / float(height)
            out["sx"] = px * scale
            out["sy"] = py * scale
        return out

    # 直接在原始分辨率下分析：距离变换在原始空间最具判别力（尖端是单薄点、
    # 箭头头部是厚块），凸包分析也无需上采样（块上采样会把所有边界点都变成
    # 厚度 1 的薄点，反而丢失判别信息）。
    h, w = height, width

    ys, xs = np.where(mask)
    cx = float(xs.mean())
    cy = float(ys.mean())

    sym_x = symmetry_iou(mask, "x")
    sym_y = symmetry_iou(mask, "y")

    symmetric = (sym_x >= SYMMETRY_IOS) and (sym_y >= SYMMETRY_IOS)

    if symmetric:
        cand_x, cand_y = cx, cy
        method = "centroid"
        shape = "symmetric"
        confidence = min(sym_x, sym_y)
    else:
        # 非对称：用凸包最薄顶点（尖端）+ 左上优先作为焦点候选。
        tip = _tip_vertex(mask, cx, cy) if _HAS_CV2 else (cx, cy)
        cand_x, cand_y = float(tip[0]), float(tip[1])
        method = "tip"
        asym = 1.0 - min(sym_x, sym_y)
        span = math.hypot(w, h)
        tip_dist = math.hypot(cand_x - cx, cand_y - cy)
        dist_conf = min(1.0, tip_dist / (span * 0.5 + 1e-6))
        confidence = 0.5 * asym + 0.5 * dist_conf
        shape = _classify_shape(cand_x, cand_y, cx, cy, w, h, sym_x, sym_y)

    # 局部质心精修（原始分辨率）
    refined_x, refined_y = _refine(cand_x, cand_y, alpha, mask, w, h)

    nx = refined_x / float(width) if width > 0 else 0.0
    ny = refined_y / float(height) if height > 0 else 0.0

    out = dict(result_base)
    out.update({
        "x": refined_x,
        "y": refined_y,
        "nx": nx,
        "ny": ny,
        "method": method,
        "confidence": float(max(0.0, min(1.0, confidence))),
        "shape": shape,
        "symmetry_x": sym_x,
        "symmetry_y": sym_y,
    })

    if target_height and height > 0:
        scale = float(target_height) / float(height)
        out["sx"] = refined_x * scale
        out["sy"] = refined_y * scale

    return out


def _tip_vertex(mask, cx, cy):
    """在原始分辨率下，用凸包最薄顶点（尖端）作为焦点候选。

    评分 = 距离变换（越薄越小）+ 轻微左上偏好（x+y 小，箭头尖端常见约定）
    + 轻微远离质心偏好。返回 (x, y)。当 cv2 缺失或顶点过少时回退质心。
    """
    ys, xs = np.where(mask)
    n = xs.size
    if n < 3 or not _HAS_CV2:
        return (cx, cy)

    H, W = mask.shape
    m = np.zeros((H, W), np.uint8)
    m[ys, xs] = 255
    dist = cv2.distanceTransform(m, cv2.DIST_L2, 5)
    pts = np.column_stack([xs, ys]).astype(np.int32)
    hull = cv2.convexHull(pts).reshape(-1, 2)

    best = (cx, cy)
    best_score = float("inf")
    for p in hull:
        dt = float(dist[p[1], p[0]])
        score = dt + 0.004 * (p[0] + p[1]) + 0.002 * math.hypot(p[0] - cx, p[1] - cy)
        if score < best_score:
            best_score = score
            best = (float(p[0]), float(p[1]))

    return best


def _classify_shape(tip_x, tip_y, cx, cy, width, height, sym_x, sym_y):
    """粗略形状分类，仅用于报告/调试。"""
    rel_x = (tip_x - cx) / float(width) if width else 0.0
    rel_y = (tip_y - cy) / float(height) if height else 0.0
    # 尖端位于左上（负方向）→ 箭头；位于下方 → 手指尖
    if rel_x < -0.1 and rel_y < -0.1:
        return "arrow"
    if rel_y > 0.15:
        return "hand"
    if sym_x >= 0.6 and sym_y < 0.6:
        return "ibeam"
    if sym_x >= 0.6 and sym_y >= 0.6:
        return "crosshair"
    return "unknown"


def _refine(cx, cy, alpha, mask, width, height):
    """候选点周围小半径内，按 alpha 加权求质心（亚像素稳定）。alpha 为 2D 数组。"""
    x0 = max(0, int(math.floor(cx - REFINE_RADIUS)))
    x1 = min(width - 1, int(math.ceil(cx + REFINE_RADIUS)))
    y0 = max(0, int(math.floor(cy - REFINE_RADIUS)))
    y1 = min(height - 1, int(math.ceil(cy + REFINE_RADIUS)))

    if x1 < x0 or y1 < y0:
        return float(cx), float(cy)

    sub_mask = mask[y0:y1 + 1, x0:x1 + 1]
    sub_alpha = alpha[y0:y1 + 1, x0:x1 + 1].astype(np.float32)

    weights = np.where(sub_mask, sub_alpha, 0.0)
    total = weights.sum()
    if total <= 0:
        return float(cx), float(cy)

    gx, gy = np.meshgrid(
        np.arange(x0, x1 + 1, dtype=np.float64),
        np.arange(y0, y1 + 1, dtype=np.float64),
    )
    rx = float((gx * weights).sum() / total)
    ry = float((gy * weights).sum() / total)
    return rx, ry


def main():
    parser = argparse.ArgumentParser(description="AngryMouse smart cursor hotspot detector")
    parser.add_argument("image", help="Path to cursor PNG (with alpha)")
    parser.add_argument("--target-height", type=int, default=None,
                        help="If set, also output the hotspot scaled to this pixel height")
    parser.add_argument("--role", type=str, default=None,
                        help="Cursor role key (e.g. arrow, hand, ibeam). Uses the role "
                             "convention table when known, else falls back to geometry.")
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON")
    args = parser.parse_args()

    if not os.path.isfile(args.image):
        print(json.dumps({"error": "file not found: " + args.image}, ensure_ascii=False))
        return 2

    try:
        result = detect(args.image, args.target_height, args.role)
    except Exception as ex:  # noqa: BLE001
        print(json.dumps({"error": str(ex)}, ensure_ascii=False))
        return 3

    if args.pretty:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
