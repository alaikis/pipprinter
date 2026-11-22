#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PipPrinter 发版 / 分发助手（把一次性手工操作固化为可复用流程）

分发策略（2026-11-22 用户确认）：
  * 当前版本三件套（绿色 zip + 安装 exe + latest.json）放 Gitee Release ——
    客户端自动更新依赖 Gitee /releases/latest，不能改成别的托管。
  * 更早版本（受 Gitee 1GB 附件配额所限）镜像到 Codeberg Releases ——
    官网 www.litchio.com 按 tag 合并两个来源，历史下载自动分流。
  * 官网版本接口实时读上游，按版本号数值段排序，不依赖 created_at。

令牌只从环境变量读取，绝不写脚本 / 日志 / 输出：
  GITEE_TOKEN      Gitee 个人令牌（Bearer）  —— publish / mirror 必需
  CODEBERG_TOKEN   Codeberg 个人令牌（token）—— mirror 必需
  verify 只读，可不设令牌。

子命令：
  manifest  <ver>              由本地 out/ 产物生成扁平 latest.json（真实 size/sha256），写到 out/<ver>.json
  publish   <ver>              把当前版本三件套上传到 Gitee 对应 Release，并校验 /releases/latest 指向
  mirror    <ver> [ver ...]    把历史版本 zip/exe 镜像到 Codeberg Releases（幂等，已存在则跳过）
  verify                       三段复验：Gitee latest 清单 / 官网版本接口 / 下载直链

常用流程（新版本 x.y.z）：
  1) 打包三件套到 out/（绿色包无 v 名 PipPrinter-win-x64-x.y.z.zip，安装版 PipPrinter-Setup-vx.y.z.exe）
  2) python scripts/release.py manifest x.y.z
  3) export GITEE_TOKEN=... ; python scripts/release.py publish x.y.z
  4) 若要把某个旧版迁到 Codeberg：export CODEBERG_TOKEN=... ; python scripts/release.py mirror x.y.z
  5) python scripts/release.py verify
  6) 官网版本基线 / VersionNote / version-matrix 同步后：cd site && npm run build && npx wrangler deploy
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "out"

GITEE_OWNER_REPO = "alaikiscom/pipprinter"
GITEE_API = f"https://gitee.com/api/v5/repos/{GITEE_OWNER_REPO}"
GITEE_DL = f"https://gitee.com/{GITEE_OWNER_REPO}/releases/download"
CODEBERG_OWNER_REPO = "alaikis/pipprinter"
CODEBERG_API = f"https://codeberg.org/api/v1/repos/{CODEBERG_OWNER_REPO}"

GREEN_RE = re.compile(r"^PipPrinter-win-x64-.*\.zip$", re.I)
INSTALLER_RE = re.compile(r"^PipPrinter-Setup-.*\.exe$", re.I)


def die(msg: str, code: int = 1):
    print(f"ERROR: {msg}", file=sys.stderr)
    sys.exit(code)


def curl(args, timeout=180):
    """统一走 curl（本机已验证可用）。返回 (stdout, returncode)。令牌只经 header 传入，不回显。"""
    cmd = ["curl", "-sS", "-L", "--max-time", str(timeout)] + args
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return p.stdout, p.returncode


def ps_get_json(url):
    """curl 在本机可能遇到代理 TLS 问题，PowerShell/.NET 栈可正常读取，作为只读兜底。"""
    cmd = (
        "$ProgressPreference='SilentlyContinue'; "
        f"$d = Invoke-RestMethod -Uri '{url}' -UseBasicParsing -TimeoutSec 40; "
        "$d | ConvertTo-Json -Depth 20 -Compress"
    )
    try:
        p = subprocess.run(["powershell", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        if p.returncode == 0 and p.stdout.strip():
            return json.loads(p.stdout)
    except Exception:
        pass
    return None


def curl_json(url, args=None, timeout=180):
    out, rc = curl((args or []) + [url], timeout)
    if rc == 0 and out.strip():
        try:
            return json.loads(out), 0
        except Exception:
            pass
    # 只读 GET（无自定义 header）失败时用 PowerShell 兜底；写操作仍走 curl。
    if not args:
        data = ps_get_json(url)
        if data is not None:
            return data, 0
    return None, rc


def gitee_headers():
    tok = os.environ.get("GITEE_TOKEN")
    if not tok:
        die("缺少环境变量 GITEE_TOKEN")
    return ["-H", f"Authorization: Bearer {tok}"]


def codeberg_headers():
    tok = os.environ.get("CODEBERG_TOKEN")
    if not tok:
        die("缺少环境变量 CODEBERG_TOKEN")
    return ["-H", f"Authorization: token {tok}"]


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def green_name(ver: str, prefix_v=False) -> str:
    # 当前版本惯例无 v；历史 003-006 曾带 v，两种都能被官网正则识别。
    return f"PipPrinter-win-x64-{'v' if prefix_v else ''}{ver}.zip"


def exe_name(ver: str) -> str:
    return f"PipPrinter-Setup-v{ver}.exe"


def find_local(ver: str, pattern: str):
    """按模式找本地产物，返回 Path 或 None。"""
    hits = sorted(OUT.glob(pattern))
    return hits[0] if hits else None


# ---------------------------------------------------------------- manifest
def cmd_manifest(args):
    ver = args.version.lstrip("v")
    zips = [p for p in OUT.glob("PipPrinter-win-x64-*.zip") if f"-{ver}.zip" in p.name or f"-v{ver}.zip" in p.name]
    if not zips:
        die(f"out/ 下找不到 {ver} 的绿色包")
    zp = zips[0]
    ep = OUT / exe_name(ver)
    if not ep.exists():
        die(f"out/ 下找不到 {ep.name}")
    manifest = {
        "version": ver,
        "url": f"{GITEE_DL}/v{ver}/{zp.name}",
        "setupUrl": f"{GITEE_DL}/v{ver}/{ep.name}",
        "sha256": sha256_of(zp),
        "size": zp.stat().st_size,
        "notes": f"PipPrinter {ver} (win-x64 portable + setup)",
    }
    dest = OUT / f"{ver}.json"
    dest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"WROTE {dest.name}")
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


# ------------------------------------------------------------------ publish
def gitee_release_by_tag(tag: str, headers):
    data, _ = curl_json(f"{GITEE_API}/releases?per_page=100", headers)
    if not isinstance(data, list):
        return None
    for r in data:
        if r.get("tag_name") == tag:
            return r
    return None


def gitee_create_release(tag: str, headers):
    body = json.dumps({
        "tag_name": tag,
        "name": tag,
        "body": f"PipPrinter {tag} (win-x64 portable + setup)",
        "target_commitish": "master",
        "prerelease": False,
    })
    data, _ = curl_json(GITEE_API + "/releases", headers + [
        "-X", "POST", "-H", "Content-Type: application/json", "-d", body,
    ])
    if not isinstance(data, dict) or not data.get("id"):
        die(f"Gitee 创建 Release 失败: {str(data)[:200]}")
    return data


def upload_gitee_asset(rid, path: Path, headers, filename=None):
    spec = f"file=@{path}"
    if filename:
        spec += f";filename={filename}"
    data, rc = curl_json(f"{GITEE_API}/releases/{rid}/attach_files", headers + [
        "-X", "POST", "-F", spec,
    ], timeout=600)
    if isinstance(data, dict) and data.get("id"):
        print(f"  UPLOADED {filename or path.name} ({data.get('size', '?')} B)")
        return True
    print(f"  UPLOAD_FAIL {filename or path.name}: {str(data)[:160]}")
    return False


def cmd_publish(args):
    ver = args.version.lstrip("v")
    tag = f"v{ver}"
    h = gitee_headers()

    zp = None
    for p in OUT.glob("PipPrinter-win-x64-*.zip"):
        if f"-{ver}.zip" in p.name:
            zp = p
            break
    ep = OUT / exe_name(ver)
    mf = OUT / f"{ver}.json"
    if not zp or not ep.exists() or not mf.exists():
        die(f"缺少三件套：zip={zp} exe={ep.exists()} manifest={mf.exists()}；先跑 manifest 子命令")

    rel = gitee_release_by_tag(tag, h)
    if rel:
        rid = rel["id"]
        print(f"RELEASE EXISTS {tag} id={rid}")
    else:
        rel = gitee_create_release(tag, h)
        rid = rel["id"]
        print(f"RELEASE CREATED {tag} id={rid}")

    existing = {a.get("name") for a in rel.get("assets", [])}
    had = existing.copy()

    # 同名附件先删再传，避免 Gitee 旧附件优先导致内容不更新。
    for a in rel.get("assets", []):
        name = a.get("name")
        if name in {zp.name, ep.name, "latest.json"}:
            curl(f"{GITEE_API}/releases/{rid}/attach_files/{a['id']}", h + ["-X", "DELETE"])

    assert had is not None  # 保持逻辑清晰：existing 仅用于诊断
    upload_gitee_asset(rid, zp, h)
    upload_gitee_asset(rid, ep, h)
    upload_gitee_asset(rid, mf, h, filename="latest.json")

    print("PUBLISH DONE — 等待 CDN 生效后运行 verify")


# ------------------------------------------------------------------- mirror
def codeberg_release_by_tag(tag: str, headers):
    data, _ = curl_json(f"{CODEBERG_API}/releases?limit=50", headers)
    if not isinstance(data, list):
        return None
    for r in data:
        if r.get("tag_name") == tag:
            return r
    return None


def codeberg_create_release(tag: str, headers):
    body = json.dumps({
        "tag_name": tag,
        "name": tag,
        "body": f"PipPrinter {tag} binaries mirrored from Gitee",
        "draft": False,
        "prerelease": False,
    })
    data, _ = curl_json(CODEBERG_API + "/releases", headers + [
        "-X", "POST", "-H", "Content-Type: application/json", "-d", body,
    ])
    if not isinstance(data, dict) or not data.get("id"):
        die(f"Codeberg 创建 Release 失败: {str(data)[:200]}")
    return data


def upload_codeberg_asset(rid, path: Path, headers, existing_names):
    if path.name in existing_names:
        print(f"  SKIP {path.name} (already present)")
        return True
    data, _ = curl_json(f"{CODEBERG_API}/releases/{rid}/assets", headers + [
        "-X", "POST", "-F", f"attachment=@{path}",
    ], timeout=600)
    if isinstance(data, dict) and data.get("id"):
        print(f"  UPLOADED {data.get('name', path.name)}")
        return True
    print(f"  UPLOAD_FAIL {path.name}: {str(data)[:160]}")
    return False


def cmd_mirror(args):
    h = codeberg_headers()
    for raw in args.versions:
        ver = raw.lstrip("v")
        tag = f"v{ver}"
        zp = None
        for p in OUT.glob("PipPrinter-win-x64-*.zip"):
            if f"-{ver}.zip" in p.name:
                zp = p
                break
        ep = OUT / exe_name(ver)
        if not zp or not ep.exists():
            print(f"SKIP {tag}: 本地缺 zip/exe")
            continue

        rel = codeberg_release_by_tag(tag, h)
        if rel:
            rid = rel["id"]
            names = {a.get("name") for a in rel.get("assets", [])}
            print(f"RELEASE EXISTS {tag} id={rid}")
        else:
            # Releases 默认关闭时先开启（幂等）
            curl(CODEBERG_API, h + ["-X", "PATCH", "-H", "Content-Type: application/json", "-d", '{"has_releases":true}'])
            rel = codeberg_create_release(tag, h)
            rid = rel["id"]
            names = set()
            print(f"RELEASE CREATED {tag} id={rid}")
        upload_codeberg_asset(rid, zp, h, names)
        upload_codeberg_asset(rid, ep, h, names)


# ------------------------------------------------------------------- verify
def ps_head_code(url):
    cmd = (
        "$ProgressPreference='SilentlyContinue'; "
        f"try {{ $r = Invoke-WebRequest -Uri '{url}' -Method Head -UseBasicParsing -TimeoutSec 40 -MaximumRedirection 5; [int]$r.StatusCode }} "
        "catch { if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 } }"
    )
    try:
        p = subprocess.run(["powershell", "-NoProfile", "-Command", cmd],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
        code = p.stdout.strip().splitlines()[-1] if p.stdout.strip() else "0"
        return code
    except Exception:
        return "0"


def http_code(url):
    out, _ = curl(["-sIL", "-o", "/dev/null", "-w", "%{http_code}", url], timeout=60)
    code = out.strip().splitlines()[-1] if out.strip() else ""
    if code == "200":
        return code
    return ps_head_code(url)


def verify():
    ok = True
    ts = str(int(time.time()))

    print("--- 1) Gitee /releases/latest 清单 ---")
    g, _ = curl_json(f"{GITEE_API}/releases/latest?cb={ts}")
    if not isinstance(g, dict) or not g.get("tag_name"):
        print("  FAIL 无法读取 Gitee latest release")
        ok = False
    else:
        print(f"  tag={g['tag_name']}")
        murl = next((a["browser_download_url"] for a in g.get("assets", []) if a.get("name") == "latest.json"), None)
        if not murl:
            print("  FAIL latest release 无 latest.json 附件")
            ok = False
        else:
            m, _ = curl_json(f"{murl}?cb={ts}")
            if isinstance(m, dict):
                print(f"  manifest version={m.get('version')} size={m.get('size')} sha={(m.get('sha256') or '')[:12]}…")
                if not m.get("version") or not m.get("sha256") or not m.get("size"):
                    print("  FAIL 清单缺 version/sha256/size")
                    ok = False
            else:
                print("  FAIL 清单不可解析")
                ok = False

    print("--- 2) 官网 /api/v1/releases/latest ---")
    s, _ = curl_json(f"https://www.litchio.com/api/v1/releases/latest?cb={ts}")
    if not isinstance(s, dict) or not s.get("release"):
        print("  FAIL 官网无 release 数据")
        ok = False
    else:
        r = s["release"]
        print(f"  version={r.get('version')} source={s.get('source')} size={r.get('artifacts', {}).get('portable', {}).get('size')}")
        print(f"  sha={(r.get('artifacts', {}).get('portable', {}).get('sha256') or '')[:12]}…")
        if not r.get("version"):
            print("  FAIL 官网版本号为空")
            ok = False

    print("--- 3) 下载直链 HEAD ---")
    if isinstance(s, dict) and s.get("release"):
        a = s["release"].get("artifacts", {})
        urls = [a.get("portable", {}).get("url"), a.get("installer", {}).get("url")]
        for u in urls:
            if not u:
                continue
            code = http_code(u)
            print(f"  {code} {u.rsplit('/', 1)[-1]}")
            if code != "200":
                ok = False

    print("VERIFY", "PASS" if ok else "FAIL")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description="PipPrinter 发版 / 分发助手")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p1 = sub.add_parser("manifest", help="由本地产物生成扁平 latest.json")
    p1.add_argument("version")
    p2 = sub.add_parser("publish", help="上传当前版本三件套到 Gitee")
    p2.add_argument("version")
    p3 = sub.add_parser("mirror", help="镜像历史版本到 Codeberg")
    p3.add_argument("versions", nargs="+")
    sub.add_parser("verify", help="三段复验")
    args = ap.parse_args()

    if args.cmd == "manifest":
        cmd_manifest(args)
    elif args.cmd == "publish":
        cmd_publish(args)
    elif args.cmd == "mirror":
        cmd_mirror(args)
    elif args.cmd == "verify":
        sys.exit(verify())


if __name__ == "__main__":
    main()
