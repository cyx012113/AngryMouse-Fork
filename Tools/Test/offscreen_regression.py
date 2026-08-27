#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
离屏回归测试（Offscreen Regression Test）
========================================
不启动任何 GUI，直接模拟 C# HotspotDetectionService 的调用路径：
  1. 对每张内置光标 PNG，用子进程调用 hotspot_detector.py
     （参数与 C# 完全一致：--role <角色键> --target-height 254）。
  2. 用与 C# ParseJson 相同的正则解析 stdout 的 JSON，取出 nx/ny/sx/sy/confidence。
  3. 计算 254 空间基线（复现 C# CalculatePngHotspot），断言
     偏移 = detected - baseline 在容差内（≈0），即“自动识别焦点”按钮结果正确。
  4. 同时验证无 --role 的几何回退路径不崩溃且返回合法 JSON。

全部在内存/子进程中完成，无需显示器，可作为 CI 或本地回归入口。
"""

import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
GEN = os.path.join(ROOT, "Tools", "CursorGen")
sys.path.insert(0, os.path.join(ROOT, "Tools", "HotspotDetector"))
sys.path.insert(0, GEN)

import generate_collections as gc  # noqa: E402

COLLECTIONS = ["Xfce4", "DMZ", "Nord"]
ROLES = gc.ROLES
TARGET_H = gc.RUNTIME_TARGET_H

# 与 C# HotspotDetectionService.ParseJson 完全一致的数字正则
JSON_NUMBER = re.compile(r'"(?P<key>[A-Za-z_]+)"\s*:\s*(?P<val>-?\d+(?:\.\d+)?)')


def find_python():
    for cand in [sys.executable, "python3", "python"]:
        try:
            subprocess.run([cand, "--version"], capture_output=True, timeout=10, check=True)
            return cand
        except Exception:
            continue
    return None


def find_script():
    cand = os.path.join(ROOT, "Tools", "HotspotDetector", "hotspot_detector.py")
    return cand if os.path.isfile(cand) else None


def parse_like_csharp(stdout):
    """模仿 C# ParseJson：只取数字字段，error 优先。"""
    err = re.search(r'"error"\s*:\s*"(?P<msg>[^"]*)"', stdout)
    if err:
        return {"success": False, "error": err.group("msg")}
    out = {}
    for m in JSON_NUMBER.finditer(stdout):
        out[m.group("key")] = float(m.group("val"))
    if "nx" not in out and "ny" not in out:
        return {"success": False, "error": "无法解析输出"}
    if out.get("sx", 0) == 0 and out.get("sy", 0) == 0:
        out["sx"] = out.get("nx", 0) * TARGET_H
        out["sy"] = out.get("ny", 0) * TARGET_H
    out["success"] = True
    return out


def main():
    py = find_python()
    script = find_script()
    if py is None or script is None:
        print("SKIP: python=%s script=%s" % (py, script))
        return 2

    print("python=%s\nscript=%s\ntarget_height=%d\n" % (py, script, TARGET_H))

    passed = 0
    failed = 0
    failures = []
    fallback_ok = 0

    for style in COLLECTIONS:
        cdir = os.path.join(ROOT, "AngryMouse", "Resources", "CursorCollections", style)
        for role in ROLES:
            png = os.path.join(cdir, "Rendered", role + ".png")
            if not os.path.isfile(png):
                failures.append("%s/%s MISSING" % (style, role))
                failed += 1
                continue

            # 1) 角色约定路径（C# 实际走的路径）
            proc = subprocess.run(
                [py, script, png, "--target-height", str(TARGET_H), "--role", role],
                capture_output=True, text=True, timeout=30)
            res = parse_like_csharp(proc.stdout)
            if not res.get("success"):
                failures.append("%s/%s DETECT_FAIL: %s" % (style, role, res.get("error")))
                failed += 1
                continue

            base = gc.angrys_base(role, png)  # 复现 C# 254 空间基线

            # 读取分发包自带偏移（254 空间），运行时实际热点 = baseline + offset
            settings_path = os.path.join(cdir, "cursor-settings.txt")
            off_x = off_y = 0
            if os.path.isfile(settings_path):
                for line in open(settings_path, encoding="utf-8"):
                    line = line.strip()
                    if line.startswith("#") or "=" not in line:
                        continue
                    k, v = line.split("=", 1)
                    if k == role + ".hotspotOffsetX":
                        off_x = float(v)
                    elif k == role + ".hotspotOffsetY":
                        off_y = float(v)

            # 运行时真实热点（与 C# ScaleHotspot 一致：baseline + offset）
            true_x = base[0] + off_x
            true_y = base[1] + off_y

            # “自动识别焦点”按钮：det - base 应≈已发货偏移（即 det≈运行时真实热点）
            off_x_det = res["sx"] - base[0]
            off_y_det = res["sy"] - base[1]
            err = ((res["sx"] - true_x) ** 2 + (res["sy"] - true_y) ** 2) ** 0.5
            off_err = ((off_x_det - off_x) ** 2 + (off_y_det - off_y) ** 2) ** 0.5

            # 2) 几何回退路径（无 role）不应崩溃且返回合法 JSON
            proc2 = subprocess.run(
                [py, script, png, "--target-height", str(TARGET_H)],
                capture_output=True, text=True, timeout=30)
            res2 = parse_like_csharp(proc2.stdout)
            if res2.get("success"):
                fallback_ok += 1

            tol = 2.0
            if err <= tol and off_err <= tol:
                passed += 1
            else:
                failed += 1
                failures.append("%s/%s err=%.1f (det=(%.1f,%.1f) true=(%.1f,%.1f) base=(%.1f,%.1f) shipped_off=(%.1f,%.1f) det_off=(%.1f,%.1f))"
                                % (style, role, err, res["sx"], res["sy"], true_x, true_y,
                                   base[0], base[1], off_x, off_y, off_x_det, off_y_det))

    print("== 离屏回归结果 ==")
    print("  角色约定路径（auto-detect 按钮）: PASS=%d FAIL=%d (容差 %.1fpx)" % (passed, failed, tol))
    print("  几何回退路径（无 role，不崩溃） : OK=%d/%d" % (fallback_ok, len(COLLECTIONS) * len(ROLES)))
    if failures:
        print("\n失败项:")
        for f in failures:
            print("  - " + f)

    if failed == 0 and fallback_ok == len(COLLECTIONS) * len(ROLES):
        print("\n结论: 全部通过 ✅（偏移误差均在容差内，自动识别焦点功能正确）")
        return 0
    print("\n结论: 存在失败项 ❌")
    return 1


if __name__ == "__main__":
    sys.exit(main())
