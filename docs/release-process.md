# PipPrinter 发版与分发流程（SOP）

> 2026-11-22 固化。背景：此前分发全靠一次性手工操作，反复踩到 latest 指向乱序、
> latest.json 格式不匹配、Gitee 附件配额爆满三个坑。本 SOP + `scripts/release.py`
> 把流程固定下来。**令牌只从环境变量读取，绝不写入脚本 / 文档 / git。**

## 一、分发策略（谁放哪）

| 版本 | 托管 | 原因 |
|---|---|---|
| 当前版本三件套 | **Gitee Releases** | 客户端自动更新依赖 `Gitee /releases/latest` 找 latest.json，不能迁走 |
| 更早版本（如 1.01.003–009） | **Codeberg Releases** | Gitee 附件总配额 1 GB，放不下全部历史包 |
| 源码 | **Codeberg**（origin）+ Gitee / GitHub / 阿里云镜像 | 见 MEMORY.md |

官网 `www.litchio.com` 按 tag 合并两边资产：最新版取 Gitee，历史下载自动分流到 Codeberg。
合并逻辑在 `site/src/lib/releases.ts`（缓存 key `releases-v3`），**`site/` 不入本仓库**，官网独立部署。

## 二、令牌（环境变量，不落盘）

```bash
export GITEE_TOKEN=...       # Gitee 个人令牌，publish / verify 用（Bearer）
export CODEBERG_TOKEN=...    # Codeberg 个人令牌，mirror 用（token）
```

校验脚本无硬编码令牌：`grep -E '35ed76|b219b2|gto_' scripts/release.py` 应无输出。

## 三、新版本发布步骤

假设新版本 `1.01.013`，一条命令链走完（前四步已脚本化）：

```bash
python scripts/release.py bump   1.01.013   # ① 同步 5 处版本号
python scripts/release.py build  1.01.013   # ② 构建 + 打三件套 + 生成清单
export GITEE_TOKEN=...
python scripts/release.py publish 1.01.013   # ③ 上传 Gitee
python scripts/release.py verify             # ④ 三段复验
```

1. **升版本号**（`bump`）：自动同步 `Utils/Brand.cs`、`PipPrinter.csproj`、
   `headless/Litchio.Headless.csproj`、`headless/Program.cs`、`AoRelay/AoRelay.csproj` 五处。
   字节级替换，保留 CRLF/BOM；不会误改历史注释（如 ManagementStore.cs 的「v1.01.012 起」）。
2. **构建打包**（`build`）：`dotnet restore/build/publish`（self-contained 单文件，参数与 Gitee 流水线一致）
   → Python `zipfile` 打绿色包 → NSIS 打安装版 → 生成扁平清单。产物到 `out/`（`out/` 已 gitignore）：
   - 绿色包：`out/PipPrinter-win-x64-1.01.013.zip`（文件名**无 v**，避免被 Gitee 源码归档 `v1.01.013.zip` 覆盖）
   - 安装版：`out/PipPrinter-Setup-v1.01.013.exe`
   - 清单：`out/1.01.013.json`（`version / url / setupUrl / sha256 / size / notes`，真实值不编造）
   - 前置：dotnet 10 SDK + NSIS（`C:\Program Files (x86)\NSIS\Bin\makensis.exe`）。
     build 有版本守卫：源码版本号与目标不一致会拒绝执行，防止忘跑 bump。
3. **上传 Gitee**（`publish`）：找/建该 tag 的 Release → 删除同名旧附件（避免 Gitee 旧附件优先）→ 上传 zip/exe/latest.json。
4. **git 提交 + 打 tag + 推四端**（Codeberg 为 origin）：
   ```bash
   git add -A && git commit -m "release: v1.01.013"
   git tag v1.01.013
   # 推 origin + gitee + github + codeup（令牌从 env 注入，不写 .git/config）
   ```
5. **官网基线同步 + 部署**：更新 `site/` 的 version-matrix / VersionNote / releases-api 说明，
   然后 `cd site && npm run build && npx wrangler deploy`。
6. **复验**（`verify`，必做）：三段检查全 PASS 才算完成——① Gitee `/releases/latest` 清单
   ② 官网 `/api/v1/releases/latest` ③ 下载直链 200。

可选：把某个旧版迁到 Codeberg：
```bash
python scripts/release.py mirror 1.01.009
```
（幂等，已存在的附件跳过；Codeberg Releases 默认关闭，脚本会尝试开启。）

## 四、发版本机构建的坑（build 子命令已处理）

- **dotnet publish 可能 OOM**：本机并行编译节点会 `System.OutOfMemoryException`。
  build 已加 `-m:1` 串行编译解决。publish 输出到带版本号的独立目录 `release/publish-<ver>`，
  不删旧目录（沙箱删除保护），也避免陈旧文件混进 zip。
- **NSIS 的 `File /r office-converter/*.*` 是编译期指令**：目录为空会直接编译失败
  （运行时 `${FileExists}` 判断救不了）。build 会在 publish 目录自动放一个带扩展名的
  `office-converter/README.txt` 占位，保证能编译；真实转换器放入该目录即可随包分发。
- **makensis 不能经 Git Bash 调用**：`/Dxxx` 参数会被 MSYS 当路径改写（变成 `D:/...`）。
  build 用 Python subprocess 直调 exe，绕开 shell。
- **不分发调试符号（.pdb）**：`dotnet publish` 加 `-p:DebugType=None -p:DebugSymbols=false`
  从源头不生成 `.pdb`；打包前再清理 publish 目录残留的旧 `.pdb`（同版本二次构建目录复用），
  zipfile 兜底过滤任何 `.pdb`。绿色包与 NSIS 安装版都不夹带调试符号（避免暴露源码路径信息、
  减小体积）。此改动只影响新构建版本，已发布版本的 sha256/附件不动。
- **版本号文件是 CRLF、Brand.cs 带 BOM**：bump 用字节级替换保留原换行/BOM，不误改历史注释。

## 五、三个必记的分发坑（都踩过）

1. **Gitee `/releases/latest` 按 `created_at` 判定，不看版本号**。
   清库/批量重建 Release 会把时间戳打乱，导致 latest 指向旧版（曾指到 v1.00.002 / v1.01.003）。
   - 正常发版（按时间顺序新建）天然正确，无需干预。
   - 若重建过 Release，**必须**跑 `verify` 确认 latest 指向当前版本。
   - 官网不受此影响：已改为按版本号数值段排序（`byNewest`）。
2. **latest.json 必须扁平结构 + 真实校验值**。嵌套 `assets[]` 会让 size/sha256 变 null。
   sha256 只能来自真实产物（`manifest` 子命令现算），禁止编造。
3. **Gitee 附件总配额 1 GB**。绿色包 zip 文件名不能以 `v` 开头（`v<tag>.zip` 会被
   源码归档覆盖，几小时后直链 404）；用无 v 的 `PipPrinter-win-x64-<version>.zip`。
   历史包放 Codeberg；同名附件重传前先删旧附件。

## 六、本地网络注意

本机 Bash 环境被注入代理 `127.0.0.1:64727`，curl 对 `www.litchio.com` 的 TLS 握手会失败
（Gitee 正常）。`release.py` 的只读请求已加 PowerShell 兜底，故 `verify` 在本机可正常跑。
换环境时 curl 优先，兜底仅在失败时触发。

## 七、当前基线（最后更新 2026-11-22）

- 线上最新：**v1.01.012**（source:live / stale:false）
- 绿色包 size `97715812`、sha256 `dfd6b251d44ba726f6eb9037af9adc6e24c5eb5cb0b09bd55d7770fdbb834cfc`
- Gitee 当前 Release id 见 `verify` 输出；历史 v1.01.003–009 在 Codeberg。
