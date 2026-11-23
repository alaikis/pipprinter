# PipPrinter 发版与分发流程（SOP）

> 2026-11-22 固化。背景：此前分发全靠一次性手工操作，反复踩到 latest 指向乱序、
> latest.json 格式不匹配、Gitee 附件配额爆满三个坑。本 SOP + `scripts/release.py`
> 把流程固定下来。**令牌只从环境变量读取，绝不写入脚本 / 文档 / git。**

## 一、分发策略（谁放哪）

| 资产 | 托管 | 原因 |
|---|---|---|
| 当前版本「绿色包 + latest.json」最小时集 | **Gitee Releases** | 客户端自动更新依赖 `Gitee /releases/latest`，不能迁走；只留 ~90MB 最小时集，配额充裕 |
| 当前版本「安装版 exe」+ 历史版本包 | **GitHub Releases**（Action 发版） | Gitee 1GB 附件总配额易满；GitHub 无此上限，大文件与历史包都放这里 |
| 更早版本（备用镜像） | **Codeberg Releases** | GitHub 不可用时的兜底；官网可据此分流 |
| 源码 | **Codeberg**（origin）+ Gitee / GitHub / 阿里云镜像 | 见 MEMORY.md |

官网 `www.litchio.com` 按 tag 合并上游资产：最新版取 Gitee，历史下载自动分流 Codeberg/GitHub。
合并逻辑在 `site/src/lib/releases.ts`（缓存 key `releases-v3`），**`site/` 不入本仓库**，官网独立部署。
大文件发版走 `.github/workflows/release.yml`（打 tag 触发，含 e2e 门禁，详见「五」）。

## 二、令牌（环境变量，不落盘）

```bash
export GITEE_TOKEN=...       # Gitee 个人令牌，publish / verify 用（Bearer）
export CODEBERG_TOKEN=...    # Codeberg 个人令牌，mirror 用（token）
```

校验脚本无硬编码令牌：`grep -E '35ed76|b219b2|gto_' scripts/release.py` 应无输出。

## 三、新版本发布步骤（所有发版先 e2e，不过不发）

**总原则：build 之后、publish 之前必须跑 `e2e` 端到端冒烟；e2e FAIL 一律停手，不得 publish。**
e2e 起 build 出的绿色包主程序（拷贝副本 + 注入 127.0.0.1 + e2e 专用 apiToken + 关自启，不污染
build 产物与用户真实配置），打核心 REST 端点并校验契约，PASS 才放行。

假设新版本 `1.01.013`，命令链（前五步已脚本化）：

```bash
python scripts/release.py bump   1.01.013   # ① 同步 5 处版本号
python scripts/release.py build  1.01.013   # ② 构建 + 打三件套 + 生成清单
python scripts/release.py e2e   1.01.013     # ③ 端到端冒烟（必过，失败退出码 1）
export GITEE_TOKEN=...
python scripts/release.py publish 1.01.013   # ④ 上传 Gitee
python scripts/release.py verify             # ⑤ 三段复验
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
3. **端到端冒烟**（`e2e`，**发版门禁，必做**）：起绿色包主程序副本打核心 REST、校验契约（见「四、e2e 冒烟范围」）。
   - 全 PASS（退出码 0）才能进入 publish；FAIL（退出码 1）立即停手，定位后再发。
   - 覆盖：`/health`（版本/uptime）、`/api/status`、`/api/printers`（须含 `data.source ∈ gdi/wmi/none`）、
     `/api/commands`、`/api/disk`、`/api/machine`、`/api/command` 空调用（省略 cmd 回落 AutoPrint，
     无内容应 400+error，而非 404 未知命令——证明回落契约在位）。
   - 契约硬校验：所有 `/api/*` 响应含 `Result.code`；`/api/printers.data.source` 必在 gdi/wmi/none。
4. **上传 Gitee**（`publish`）：找/建该 tag 的 Release → 删除同名旧附件（避免 Gitee 旧附件优先）→ 上传 zip/exe/latest.json。
5. **git 提交 + 打 tag + 推四端**（Codeberg 为 origin）：
   ```bash
   git add -A && git commit -m "release: v1.01.013"
   git tag v1.01.013
   # 推 origin + gitee + github + codeup（令牌从 env 注入，不写 .git/config）
   ```
6. **官网基线同步 + 部署**：更新 `site/` 的 version-matrix / VersionNote / releases-api 说明，
   然后 `cd site && npm run build && npx wrangler deploy`。
7. **复验**（`verify`，必做）：三段检查全 PASS 才算完成——① Gitee `/releases/latest` 清单
   ② 官网 `/api/v1/releases/latest` ③ 下载直链 200。

可选：把某个旧版迁到 Codeberg：
```bash
python scripts/release.py mirror 1.01.009
```
（幂等，已存在的附件跳过；Codeberg Releases 默认关闭，脚本会尝试开启。）

## 四、e2e 冒烟范围（发版门禁）

`python scripts/release.py e2e <ver>` 是发版前置硬门禁。行为与范围：

- **副本隔离**：把 `release/publish-<ver>/` 整目录拷到 `out/.e2e-<ver>/`，在副本里写
  `config/appsettings.json`（`webHost=127.0.0.1` + 随机 e2e 专用 `apiToken` + `autoStart=false` + `logLevel=WARN`），
  起副本主程序。不动 build 产物、不动用户真实配置；结束后逐文件清副本（含 token）。
- **为什么注入 127.0.0.1 + token**：绿色包默认 `webHost=0.0.0.0` 且无 apiToken，客户端启动门禁
  （「非本机监听必须配 apiToken」）会拒绝起 Web。e2e 强制回环 + 专用 token 让 Web 起来，且不留后门。
- **就绪轮询**：`/health` 轮询（默认等 60s，CI 可 `--wait 90`）拿到 `version`/`uptimeSeconds` 才算起好；
  主程序提前退出 → 直接判 FAIL。
- **端点冒烟（带 e2e token）**：
  | 端点 | 校验 |
  |---|---|
  | `/health` | `version` 非空（应等于 `<ver>`） |
  | `/api/status` `/api/commands` `/api/disk` `/api/machine` | 返回 JSON 且含 `Result.code` |
  | `/api/printers` | `Result.code=200`；`data.source ∈ {gdi,wmi,none}`（013 根治契约，GDI+ 失败回退 WMI） |
  | `/api/command` 空调用 `{}` | 省略 cmd 回落 AutoPrint；无内容应 `400`+`error`（而非 404 未知命令） |
- **契约硬断言（任一不过即 FAIL，退出码 1）**：所有 `/api/*` 含 `Result.code`；`/api/printers.data.source`
  必在 gdi/wmi/none。FAIL 时打印定位提示（端口占用 / 旧版常驻进程 / ProbeStaleInstance）。
- **本地可独立跑**：本机此刻 19861 空闲时即可 `e2e` 实测（已验证 v1.01.013 全 PASS、`source=gdi`、4 台打印机）。

## 五、GitHub Action 发版（大文件镜像 + 根治空间配额）

**背景：Gitee 为何总空间不够**——Gitee 社区版**附件总配额 1GB**（单附件 100MB）是平台硬上限，
历史包 + 当前三件套一累加就满，需反复清历史附件腾额度（2026-11-24 已全清 001–012）。
**GitHub 没有 1GB 附件总配额**——Release 资产走制品存储，单 Release 2GB、账户无附件总量上限，
不会这样「爆空间」。

**`.github/workflows/release.yml`**（打 tag `v1.01.0XX` 触发，windows-latest）：

```
checkout → .NET 10 SDK(choco) → NSIS(choco, 装到 release.py 期望路径)
→ bump 对齐 tag → build 三件套 → e2e 门禁（FAIL 即阻断）
→ GitHub Release 传「绿色包 + 安装版 + latest.json」（大文件镜像）
→ [可选] 设 GITEE_TOKEN 时补传 Gitee 最小时集「绿色包 + latest.json」（~90MB，配额充裕）
→ 复验 GitHub Release 资产 + Gitee /releases/latest
```

- **分工**：安装版 exe 与全部历史包走 **GitHub Release**（空间无忧）；Gitee 只保留
  「当前绿色包 + latest.json」两条最小时集——这是客户端自动更新 `/releases/latest` 的硬依赖，不能删。
- **e2e 在 CI 是门禁**：`e2e` FAIL 时后续 GitHub/Gitee 上传步骤不执行，坏包发不到线上。
- **前置**：GitHub 仓库 `alaikis/pipprinter` 需配 `secrets.GITEE_TOKEN`（只读+附件写权限的 PAT）；
  不配则跳过 Gitee 补传、只发 GitHub（本地可手动 `release.py publish` 兜底）。
- **与本地流程二选一**：习惯本地发版可继续 `release.py` 全流程；用 CI 发版则打 tag 触发，
  二者 e2e 门禁逻辑一致。CI 发版省去本机 NSIS/dotnet 与代理问题。

## 六、发版本机构建的坑（build 子命令已处理）

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
- **绿色包不能夹带服务端 AoRelay.exe**：`PipPrinter.sln` 里 AoRelay 是独立控制台项目，
  `dotnet publish` 整目录时会把 `AoRelay.exe`（~43MB）也发进 publish 目录，被 zip 误收后
  绿色包从 ~90MB 涨到 130MB+，超 Gitee 单附件 100MB 限制、上传失败。build 已 `rglob("AoRelay.*")`
  剔除并在 zipfile 处按 `aorelay` 前缀兜底过滤，另加 100MB 体积守卫（超限直接 die）。
- **版本号文件是 CRLF、Brand.cs 带 BOM**：bump 用字节级替换保留原换行/BOM，不误改历史注释。

## 七、三个必记的分发坑（都踩过）

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
4. **Gitee 单附件 100MB 上限 + 仓库 1GB 总配额**。绿色包（含 .NET 自包含单文件 ~90MB）压线；
   若夹带无关产物（如服务端 `AoRelay.exe`）会超 100MB 单附件上限、上传直接失败。
   仓库总配额满时，新附件（哪怕几 KB 的 latest.json）也传不上 → 旧同名记录继续服务 →
   **清单 sha/size 与真实 zip 不一致 → 客户端 SHA256 校验失败、更新卡住**。处置：
   - 先清历史附件腾额度（历史包已镜像 Codeberg，可安全删 Gitee 旧版附件）；
   - 或重建该 Release（删旧 release + 新建 + 只传绿色包与 latest.json）；
   - 自动更新只依赖绿色包 `latest.json`，安装版 exe 可暂缺、不影响更新链路，配额清理后补传。
   - publish 脚本删附件用 `a.get("id")` 兜底（配额满时 Gitee 返回缺 id 的 asset 记录）。

## 八、本地网络注意

本机 Bash 环境被注入代理 `127.0.0.1:64727`，curl 对 `www.litchio.com` 的 TLS 握手会失败
（Gitee 正常）。`release.py` 的只读请求已加 PowerShell 兜底，故 `verify` 在本机可正常跑。
换环境时 curl 优先，兜底仅在失败时触发。

## 九、当前基线（最后更新 2026-11-24）

- **发版门禁与空间策略落地（2026-11-24）**：
  - `release.py` 新增 `e2e` 子命令（副本隔离起绿色包主程序 + 127.0.0.1/专用 token，打核心 REST 校验契约）；
    发版流程 build 与 publish 之间强制 e2e，FAIL 退出码 1 阻断发布。v1.01.013 本地 e2e 全 PASS
    （`/health` 1.01.013、`/api/printers source=gdi` 4 台、`/api/command` 空调用 400+error）。
  - 新增 `.github/workflows/release.yml`：打 tag `v*` 触发，windows-latest 上 build → e2e 门禁 →
    GitHub Release（安装版 + 绿色包 + 历史包，根治 Gitee 1GB 配额）→ 可选 Gitee 最小时集补传。
  - Gitee 只保留「当前绿色包 + latest.json」最小时集；安装版与历史包转 GitHub Release 承载。
- 线上最新：**v1.01.013**（source:live / 官网 size=93488370 / sha 8bfd0347…）
- 绿色包 size `93488370`、sha256 `8bfd03475cd25b7568a67b889baa421a70ebb21c82cca1be7ce88faf40a37e86`
  （已剔除服务端 AoRelay.exe 与 .pdb）
- 安装版 exe size `67877111`、sha256 `62c02abdc52095f636a1d1186e8cbea8275b036888eca5966f22d40da1649674`
  （v1.01.013 三件套已齐：绿色包 asset 3802005 / latest.json 3802006 / 安装版 exe 3806552，下载直链全 200）
- **Gitee 配额全清落地（2026-11-24）**：为腾出 013 安装版 65MB，已删除 001–012 全部真上传附件
  （绿色包 + 安装版 exe + latest.json，共 11 条），仅保留 Gitee 自动源码归档 `vX.zip/.tar.gz`
  （无 id、随 tag 重建、不占真配额）。清理前快照存 `.gitee-backup/gitee-releases-before-fullclear.json`（已 gitignore）。
  删除走各 Release 专属 `attach_files` 端点（releases 列表端点 asset 不带 id 无法删）；013 绿色包与 latest.json 保留不动。
- Gitee 当前 Release id 见 `verify` 输出；历史 v1.01.003–009 在 Codeberg。
