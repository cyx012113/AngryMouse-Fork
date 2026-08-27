#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
一致性校验：智能热点检测器 vs 生成器的解析几何真值
====================================================
对 Xfce4 / DMZ / Nord 三种套建的 14 个角色 PNG 分别：
  1) 用 HotspotDetectionService 同款逻辑（hotspot_detector.detect_pil, --role, target=254）
     得到检测器给出的 254 空间热点 (sx, sy)。
  2) 用生成器 generate_collections.analytic_hotspot 得到几何已知真值 (true254)。
  3) 用 angrys_base 复现 C# CalculatePngHotspot 的 254 基线，并验证
     分发包 cursor-settings.txt 的偏移 = true254 - base（即分发包自带的焦点即正确）。
  4) 同时在无 --role 时跑几何回退，记录它与真值的误差，评估回退可靠性。

输出每张图的误差（像素）与最大误差，作为“检测器准确度”报告。
"""

import math
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
GEN = os.path.join(HERE, "..", "CursorGen")
sys.path.insert(0, HERE)
sys.path.insert(0, GEN)

import hotspot_detector  # noqa: E402
import generate_collections as gc  # noqa: E402

ROLES = gc.ROLES
STYLES = ["Xfce4", "DMZ", "Nord"]
RUNTIME_TARGET_H = gc.RUNTIME_TARGET_H
NATIVE = gc.NATIVE


def true254(role):
    hs = gc.analytic_hotspot(role)
    return (hs[0] * RUNTIME_TARGET_H / float(NATIVE),
            hs[1] * RUNTIME_TARGET_H / float(NATIVE))


def main():
    print("== 检测器(--role) 与几何真值对比（254 空间，单位像素）==")
    max_err = 0.0
    worst = None
    rows = []
    for style in STYLES:
        for role in ROLES:
            png = os.path.join("..", "..", "AngryMouse", "Resources", "CursorCollections",
                               style, "Rendered", role + ".png")
            png = os.path.abspath(os.path.join(HERE, png))
            if not os.path.isfile(png):
                print("MISSING", png)
                continue
            img = Image.open(png).convert("RGBA")
            det = hotspot_detector.detect_pil(img, target_height=RUNTIME_TARGET_H, role=role)
            t = true254(role)
            sx = det.get("sx")
            sy = det.get("sy")
            if sx is None or sy is None:
                # 无 target 时回退：用归一化换算
                sx = det["nx"] * RUNTIME_TARGET_H
                sy = det["ny"] * RUNTIME_TARGET_H
            err = math.hypot(sx - t[0], sy - t[1])
            max_err = max(max_err, err)
            if worst is None or err > worst[0]:
                worst = (err, style, role, det["method"], sx, sy, t[0], t[1])
            rows.append((style, role, det["method"], det["confidence"], sx, sy, t[0], t[1], err))

    for style, role, method, conf, sx, sy, tx, ty, err in rows:
        flag = "OK " if err < 3.0 else "BAD"
        print("%-6s %-11s %-8s conf=%.2f det=(%6.1f,%6.1f) true=(%6.1f,%6.1f) err=%5.1f  %s"
              % (style, role, method, conf, sx, sy, tx, ty, err, flag))

    print("\n最大误差 = %.1f px  (%s/%s, method=%s, det=(%.1f,%.1f) true=(%.1f,%.1f))"
          % (max_err, worst[1], worst[2], worst[3], worst[4], worst[5], worst[6], worst[7]))

    # 分发包自带偏移一致性校验
    print("\n== 分发包 cursor-settings.txt 偏移 = 真值 - C#基线? ==")
    bad_pack = 0
    for style in STYLES:
        cdir = os.path.abspath(os.path.join(HERE, "..", "..", "AngryMouse",
                                            "Resources", "CursorCollections", style))
        sp = os.path.join(cdir, "cursor-settings.txt")
        offsets = {}
        if os.path.isfile(sp):
            for line in open(sp, encoding="utf-8"):
                line = line.strip()
                if line.startswith("#") or "=" not in line:
                    continue
                k, v = line.split("=", 1)
                offsets[k] = int(v)
        for role in ROLES:
            png = os.path.join(cdir, "Rendered", role + ".png")
            base = gc.angrys_base(role, png)  # 复现 C# 基线
            ox = offsets.get(role + ".hotspotOffsetX", 0)
            oy = offsets.get(role + ".hotspotOffsetY", 0)
            shipped254 = (base[0] + ox, base[1] + oy)
            t = true254(role)
            err = math.hypot(shipped254[0] - t[0], shipped254[1] - t[1])
            if err >= 1.5:
                bad_pack += 1
                print("  %-6s %-11s PACK-OFFSET MISMATCH shipped254=(%.1f,%.1f) true=(%.1f,%.1f) err=%.1f"
                      % (style, role, shipped254[0], shipped254[1], t[0], t[1], err))
    if bad_pack == 0:
        print("  全部 3 套建 × 14 角色 的偏移与真值一致（误差 < 1.5px）。")

    # 几何回退（无 role）可靠性
    print("\n== 无 --role 几何回退 与真值对比（识别未知用户光标时）==")
    max_fb = 0.0
    for style in STYLES:
        for role in ROLES:
            png = os.path.abspath(os.path.join(HERE, "..", "..", "AngryMouse",
                                                "Resources", "CursorCollections",
                                                style, "Rendered", role + ".png"))
            img = Image.open(png).convert("RGBA")
            det = hotspot_detector.detect_pil(img, target_height=RUNTIME_TARGET_H)  # 无 role
            t = true254(role)
            sx = det["sx"] if det.get("sx") is not None else det["nx"] * RUNTIME_TARGET_H
            sy = det["sy"] if det.get("sy") is not None else det["ny"] * RUNTIME_TARGET_H
            err = math.hypot(sx - t[0], sy - t[1])
            max_fb = max(max_fb, err)
    print("  无 role 回退最大误差 = %.1f px" % max_fb)


if __name__ == "__main__":
    main()
