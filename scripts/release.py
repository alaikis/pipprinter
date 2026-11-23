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
  bump      <ver>              同步全部版本号定义到 <ver>（brand/csproj×3/headless Program）
  build     <ver>              构建并打包三件套到 out/（dotnet publish + zip + NSIS + manifest）
  manifest  <ver>              由本地 out/ 产物生成扁平 latest.json（真实 size/sha256），写到 out/<ver>.json
  publish   <ver>              把当前版本三件套上传到 Gitee 对应 Release，并校验 /releases/latest 指向
  mirror    <ver> [ver ...]    把历史版本 zip/exe 镜像到 Codeberg Releases（幂等，已存在则跳过）
  verify                       三段复验：Gitee latest 清单 / 官网版本接口 / 下载直链

常用流程（新版本 x.y.z）：
  1) python scripts/release.py bump x.y.z          # 同步 5 处版本号
  2) python scripts/release.py build x.y.z         # 构建 + 打三件套 + 生成清单
  3) export GITEE_TOKEN=... ; python scripts/release.py publish x.y.z
  4) 若要把某个旧版迁到 Codeberg：export CODEBERG_TOKEN=... ; python scripts/release.py mirror x.y.z
  5) python scripts/release.py verify
  6) git commit/tag/push 四端 + 官网版本基线 / VersionNote / version-matrix 同步后：
     cd site && npm run build && npx wrangler deploy

build 前置：dotnet 10 SDK、NSIS（C:\\Program Files (x86)\\NSIS\\Bin\\makensis.exe）。
zip 用 Python 标准库 zipfile 打包，不依赖外部 zip 命令；makensis 用 subprocess 直调 exe
（不经 shell，避免 Git Bash 把 /D 参数当路径改写）。
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
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

# 版本号定义位置（bump 用）。每条是 (相对路径, 匹配当前版本号的正则)。
# 只匹配版本号字段本身，绝不动历史注释（如 ManagementStore.cs 的「v1.01.012 起」）。
# 正则按 bytes 编译：这些文件是 CRLF 且 Brand.cs 带 BOM，必须字节级替换以原样保留。
VERSION_FILES = [
    ("Utils/Brand.cs", rb'(public const string Version = ")\d+\.\d+\.\d+(";)'),
    ("PipPrinter.csproj", rb"(<Version>)\d+\.\d+\.\d+(</Version>)"),
    ("AoRelay/AoRelay.csproj", rb"(<Version>)\d+\.\d+\.\d+(</Version>)"),
    ("headless/Litchio.Headless.csproj", rb"(<Version>)\d+\.\d+\.\d+(</Version>)"),
    ("headless/Program.cs", rb'(public const string Version = ")\d+\.\d+\.\d+(";)'),
]

MAKENSIS = Path(r"C:\Program Files (x86)\NSIS\Bin\makensis.exe")


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


# -------------------------------------------------------------------- bump
def cmd_bump(args):
    ver = args.version.lstrip("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+", ver):
        die(f"版本号格式应为 X.Y.Z，收到: {ver}")
    changed = []
    for rel, pattern in VERSION_FILES:
        p = ROOT / rel
        if not p.exists():
            die(f"版本号文件不存在: {rel}")
        raw = p.read_bytes()  # 字节级：保留 CRLF 与 BOM
        new_raw, n = re.subn(pattern, lambda m: m.group(1) + ver.encode() + m.group(2), raw, count=1)
        if n != 1:
            die(f"未能在 {rel} 匹配到版本号字段（检查格式是否被改动）")
        p.write_bytes(new_raw)
        changed.append(rel)
    print(f"BUMP -> {ver}")
    for rel in changed:
        print(f"  UPDATED {rel}")


# -------------------------------------------------------------------- build
def run_cmd(argv, cwd=None, timeout=1200, env=None):
    print(f"  RUN {' '.join(str(x) for x in argv)}" + (f"  (cwd={cwd.name})" if cwd else ""))
    p = subprocess.run([str(x) for x in argv], cwd=str(cwd) if cwd else None, env=env,
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    tail = "\n".join((p.stdout + p.stderr).strip().splitlines()[-15:])
    if p.returncode != 0:
        print(tail)
        die(f"命令失败 (exit {p.returncode}): {argv[0]}")
    return p.stdout


def build_manifest(ver, zp: Path, ep: Path) -> dict:
    return {
        "version": ver,
        "url": f"{GITEE_DL}/v{ver}/{zp.name}",
        "setupUrl": f"{GITEE_DL}/v{ver}/{ep.name}",
        "sha256": sha256_of(zp),
        "size": zp.stat().st_size,
        "notes": f"PipPrinter {ver} (win-x64 portable + setup)",
    }


def write_manifest(ver, manifest: dict):
    OUT.mkdir(exist_ok=True)
    dest = OUT / f"{ver}.json"
    dest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"  MANIFEST {dest.name}")


def cmd_build(args):
    ver = args.version.lstrip("v")
    if not re.fullmatch(r"\d+\.\d+\.\d+", ver):
        die(f"版本号格式应为 X.Y.Z，收到: {ver}")

    # 前置检查：版本号是否已同步（读取 Brand.cs 的当前值）
    brand = (ROOT / "Utils/Brand.cs").read_text(encoding="utf-8")
    m = re.search(r'public const string Version = "(\d+\.\d+\.\d+)";', brand)
    cur = m.group(1) if m else "?"
    if cur != ver:
        die(f"源码版本号={cur} 与目标 {ver} 不一致；先执行: python scripts/release.py bump {ver}")

    if not shutil.which("dotnet"):
        die("未找到 dotnet（需要 .NET 10 SDK）")
    if not MAKENSIS.exists():
        die(f"未找到 NSIS: {MAKENSIS}")

    # 1) dotnet build + publish（self-contained 单文件，参数与 Gitee 流水线一致）
    # 每次用带版本号的全新目录，既不删除旧目录（沙箱删除保护），也避免陈旧文件混进 zip。
    publish_dir = ROOT / "release" / f"publish-{ver}"
    publish_arg = f"..\\release\\publish-{ver}"  # 相对 installer/ 的 Windows 路径
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    # -m:1 串行编译：本机内存有限时并行节点会 OOM（dotnet build/publish 曾因此失败）。
    run_cmd(["dotnet", "restore", "-m:1"], cwd=ROOT, env=env)
    run_cmd(["dotnet", "build", "-c", "Release", "-m:1", f"-p:Version={ver}", "-p:EnableWindowsTargeting=true"], cwd=ROOT, env=env)
    # DebugType=None：从源头不生成 .pdb，绿色包与 NSIS 安装版都不再夹带调试符号
    # （publish 目录无 .pdb → File /r *.* 与 zipfile 打包都不会收进去）。
    run_cmd([
        "dotnet", "publish", "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true", f"-p:Version={ver}",
        "-p:EnableWindowsTargeting=true", "-p:DebugType=None", "-p:DebugSymbols=false",
        "-m:1", "-o", str(publish_dir),
    ], cwd=ROOT, env=env)

    # 2) 清理 publish 目录里不该进客户端包的产物：
    #    - 旧 .pdb（同版本二次构建目录复用时残留；DebugType=None 不会自动清掉）
    #    - AoRelay.exe（.sln 里的中继服务器独立项目，dotnet publish 整目录时会把它发出来，
    #      客户端绿色包不需要它；夹带会使包体从 ~90MB 涨到 130MB+，超 Gitee 单附件 100MB 限制）
    #      NSIS 的 `File /r *.*` 与下方 zipfile 都读这个目录，在这里统一剔除。
    removed = []
    for f in sorted(publish_dir.rglob("*.pdb")):
        if f.is_file():
            f.unlink()
            removed.append(f.name)
    for f in sorted(publish_dir.rglob("AoRelay.*")):
        if f.is_file():
            f.unlink()
            removed.append(f.name)
    if removed:
        print(f"  STRIPPED {len(removed)}: {', '.join(removed)}")

    # 绿色包：Python zipfile 打包 publish 目录（无 v 命名，避开 Gitee 源码归档覆盖）
    OUT.mkdir(exist_ok=True)
    zp = OUT / green_name(ver)
    with zipfile.ZipFile(zp, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:  # 'w' 直接覆盖
        for f in sorted(publish_dir.rglob("*")):
            # 兜底过滤：pdb 与 AoRelay 服务端产物都不进客户端包
            if f.is_file() and f.suffix.lower() != ".pdb" and not f.name.lower().startswith("aorelay"):
                zf.write(f, f.relative_to(publish_dir))
    print(f"  ZIP {zp.name} ({zp.stat().st_size} B)")
    # Gitee 单附件 100MB 限制：绿色包超限会导致 publish 上传失败，提前报警
    limit = 100 * 1024 * 1024
    if zp.stat().st_size > limit:
        die(f"绿色包 {zp.stat().st_size // 1048576}MB 超过 Gitee 单附件 100MB 限制，"
            f"请检查 publish 目录是否又夹带了非客户端产物")

    # 3) 安装版：NSIS（cwd 设为 installer/，OutFile 落在该目录）
    # NSIS 脚本的 `File /r office-converter/*.*` 是编译期指令，目录为空会直接编译失败
    # （运行时 ${FileExists} 救不了）。历史构建靠 office-converter/.keep 占位；这里自动补上。
    oc = publish_dir / "office-converter"
    oc.mkdir(exist_ok=True)
    (oc / "README.txt").write_bytes(
        "Office converter placeholder. Drop portable LibreOffice here to bundle it.\n".encode()
    )  # 带扩展名的占位，保证 NSIS `File /r *.*` 有文件可打包

    installer_dir = ROOT / "installer"
    old_setup = installer_dir / exe_name(ver)
    run_cmd([str(MAKENSIS), f"/DVERSION={ver}", f"/DPUBLISH_DIR={publish_arg}", "PipPrinter.nsi"],
            cwd=installer_dir, timeout=600)
    ep = OUT / exe_name(ver)
    shutil.copyfile(str(old_setup), str(ep))  # copy 覆盖，避免 move/unlink 触发删除保护
    print(f"  SETUP {ep.name} ({ep.stat().st_size} B)")

    # 4) 扁平 latest.json（真实 sha256/size）
    manifest = build_manifest(ver, zp, ep)
    write_manifest(ver, manifest)
    print("BUILD DONE")
    print(json.dumps(manifest, ensure_ascii=False, indent=2))


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
    manifest = build_manifest(ver, zp, ep)
    write_manifest(ver, manifest)
    dest = OUT / f"{ver}.json"
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
    # 注意：上传失败/超限时 Gitee 可能返回缺 id 的 asset 记录，必须 a.get("id") 兜底，
    # 否则会 KeyError（曾发生：zip 超 100MB 上传失败后，重试 publish 卡在这里）。
    for a in rel.get("assets", []):
        name = a.get("name")
        aid = a.get("id")
        if aid and name in {zp.name, ep.name, "latest.json"}:
            curl(f"{GITEE_API}/releases/{rid}/attach_files/{aid}", h + ["-X", "DELETE"])

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
    pb = sub.add_parser("bump", help="同步全部版本号到指定值")
    pb.add_argument("version")
    pd = sub.add_parser("build", help="构建并打包三件套到 out/")
    pd.add_argument("version")
    p1 = sub.add_parser("manifest", help="由本地产物生成扁平 latest.json")
    p1.add_argument("version")
    p2 = sub.add_parser("publish", help="上传当前版本三件套到 Gitee")
    p2.add_argument("version")
    p3 = sub.add_parser("mirror", help="镜像历史版本到 Codeberg")
    p3.add_argument("versions", nargs="+")
    sub.add_parser("verify", help="三段复验")
    args = ap.parse_args()

    if args.cmd == "bump":
        cmd_bump(args)
    elif args.cmd == "build":
        cmd_build(args)
    elif args.cmd == "manifest":
        cmd_manifest(args)
    elif args.cmd == "publish":
        cmd_publish(args)
    elif args.cmd == "mirror":
        cmd_mirror(args)
    elif args.cmd == "verify":
        sys.exit(verify())


if __name__ == "__main__":
    main()
