; ============================================================
; PipPrinter / 荔枝打印助手 (Lychee Printing Assistant) — NSIS 安装向导
; 公司：Alaikis.com Inc  https://alaikis.com
;
; 本地构建（Windows）：
;   makensis /DVERSION=1.00.002 /DPUBLISH_DIR=..\release\publish installer\PipPrinter.nsi
; 流水线构建（Linux，Gitee Go）：
;   makensis -DVERSION=$VERSION -DPUBLISH_DIR=../temp/publish installer/PipPrinter.nsi
;
; 特性：每用户安装（免管理员，%LOCALAPPDATA%\Programs\PipPrinter）、
;       中英双语向导、桌面/开始菜单快捷方式、开机自启（HKCU）、
;       升级自动保留用户配置（config/data/log）、完整卸载器。
; ============================================================

!include "MUI2.nsh"
!include "LogicLib.nsh"

!ifndef VERSION
  !define VERSION "1.00.001"
!endif
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\release\publish"
!endif
; 路径分隔符：Windows 默认反斜杠；Linux makensis 编译时传 -DSEP=/
!ifndef SEP
  !define SEP "\"
!endif
!ifndef COMPANY
  !define COMPANY "Alaikis.com Inc"
!endif
!ifndef PUBURL
  !define PUBURL "https://gitee.com/alaikiscom/pipprinter"
!endif

Name "荔枝打印助手 Lychee Printing Assistant ${VERSION}"
OutFile "PipPrinter-Setup-v${VERSION}.exe"
Unicode true
InstallDir "$LOCALAPPDATA\Programs\PipPrinter"
InstallDirRegKey HKCU "Software\PipPrinter" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIAddVersionKey "ProductName" "Lychee Printing Assistant · 荔枝打印助手"
VIAddVersionKey "CompanyName" "${COMPANY}"
VIAddVersionKey "LegalCopyright" "© 2026 ${COMPANY}. All rights reserved."
VIAddVersionKey "FileDescription" "荔枝打印助手 Lychee Printing Assistant 安装程序"
VIAddVersionKey "FileVersion" "${VERSION}"
VIProductVersion "1.0.3.0"

; 编译期路径分隔符用 ${SEP} 保证跨平台（Windows makensis 需反斜杠，Linux 需正斜杠）
!define MUI_ICON "..${SEP}Resource${SEP}app.ico"
!define MUI_UNICON "..${SEP}Resource${SEP}app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\PipPrinter.exe"
!define MUI_FINISHPAGE_RUN_TEXT "启动 荔枝打印助手 (Start Lychee Printing Assistant)"
!define MUI_FINISHPAGE_SHOWREADME ""
!define MUI_FINISHPAGE_LINK "访问项目主页 (Project homepage)"
!define MUI_FINISHPAGE_LINK_LOCATION "${PUBURL}"

; ---- 向导页面 ----
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "LICENSE.txt"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; ---- 语言：简体中文优先，英文备选 ----
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---- 组件描述 ----
LangString DESC_CORE      ${LANG_SIMPCHINESE} "主程序（必需）"
LangString DESC_CORE      ${LANG_ENGLISH}     "Core application (required)"
LangString DESC_SHORTCUTS ${LANG_SIMPCHINESE} "开始菜单快捷方式"
LangString DESC_SHORTCUTS ${LANG_ENGLISH}     "Start Menu shortcuts"
LangString DESC_DESKTOP   ${LANG_SIMPCHINESE} "桌面快捷方式"
LangString DESC_DESKTOP   ${LANG_ENGLISH}     "Desktop shortcut"
LangString DESC_AUTORUN   ${LANG_SIMPCHINESE} "开机自动启动（HKCU 当前用户）"
LangString DESC_AUTORUN   ${LANG_ENGLISH}     "Run at Windows startup (HKCU, current user)"
LangString DESC_OFFICE    ${LANG_SIMPCHINESE} "Office 转换器（内置 LibreOffice，用于打印 .docx/.xlsx/.pptx 等）"
LangString DESC_OFFICE    ${LANG_ENGLISH}     "Office converter (bundled LibreOffice, for .docx/.xlsx/.pptx printing)"

; ============================================================
Section "PipPrinter (required)" SEC_CORE
  SectionIn RO
  ; 若应用正在运行，先尝试结束（静默失败不影响安装）
  nsExec::Exec 'taskkill /IM PipPrinter.exe /F'

  ; 备份用户配置（升级保留）
  StrCpy $R0 ""
  ${If} ${FileExists} "$INSTDIR\config\appsettings.json"
    CopyFiles /SILENT "$INSTDIR\config\appsettings.json" "$TEMP\PipPrinter-appsettings.json"
    StrCpy $R0 "1"
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}${SEP}*.*"
  ; 发布基线：self-contained 单文件（dotnet publish --self-contained true -p:PublishSingleFile=true），
  ; 用户无需预装 .NET 运行时。若某版本发布物改为 framework-dependent，安装器必须加装
  ; .NET 10 Desktop Runtime 依赖或安装前检测并提示，禁止直接发布（用户 2026-11-15 确认）。

  ; 恢复用户配置
  ${If} $R0 == "1"
    CopyFiles /SILENT "$TEMP\PipPrinter-appsettings.json" "$INSTDIR\config\appsettings.json"
    Delete "$TEMP\PipPrinter-appsettings.json"
  ${EndIf}

  ; 卸载信息（HKCU，每用户）
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\PipPrinter" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\PipPrinter" "Version" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "DisplayName" "Lychee Printing Assistant · 荔枝打印助手"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "Publisher" "${COMPANY}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "DisplayIcon" "$INSTDIR\PipPrinter.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "URLInfoAbout" "${PUBURL}"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter" \
    "NoRepair" 1
SectionEnd

Section "开始菜单 / Start Menu" SEC_SHORTCUTS
  CreateDirectory "$SMPROGRAMS\PipPrinter"
  CreateShortcut "$SMPROGRAMS\PipPrinter\PipPrinter.lnk" "$INSTDIR\PipPrinter.exe"
  CreateShortcut "$SMPROGRAMS\PipPrinter\卸载 Uninstall.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "桌面快捷方式 / Desktop" SEC_DESKTOP
  CreateShortcut "$DESKTOP\PipPrinter.lnk" "$INSTDIR\PipPrinter.exe"
SectionEnd

Section "开机自启 / Autorun" SEC_AUTORUN
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PipPrinter" \
    "$\"$INSTDIR\PipPrinter.exe$\""
SectionEnd

; Office 转换器：仅当发布目录存在 office-converter/ 时安装（构建时可选注入）
Section "Office 转换器 / Office Converter" SEC_OFFICE
  ${If} ${FileExists} "${PUBLISH_DIR}${SEP}office-converter${SEP}*"
    CreateDirectory "$INSTDIR\lib\office"
    SetOutPath "$INSTDIR\lib\office"
    File /r "${PUBLISH_DIR}${SEP}office-converter${SEP}*.*"
    DetailPrint "Office 转换器已安装到 lib/office/"
  ${Else}
    DetailPrint "发布目录未包含 office-converter/，跳过 Office 转换器"
  ${EndIf}
SectionEnd

; ============================================================
Section "Uninstall"
  ; 卸载日志（排障用，写完后删除）
  FileOpen $9 "$TEMP\pipprinter-uninstall.log" w
  FileWrite $9 "uninstall start$\r$\n"
  nsExec::Exec 'taskkill /IM PipPrinter.exe /F'
  FileWrite $9 "taskkill done$\r$\n"
  ; 先尝试删除程序文件（保留用户数据）
  Delete "$INSTDIR\PipPrinter.exe"
  Delete "$INSTDIR\PipPrinter.exe.old-*"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\docs"
  RMDir /r "$INSTDIR\pdfreader"
  RMDir /r "$INSTDIR\Resource"
  RMDir /r "$INSTDIR\lib"
  FileWrite $9 "program files removed$\r$\n"

  MessageBox MB_YESNO|MB_ICONQUESTION \
    "是否同时删除用户数据（打印队列、设置、日志）？$\n$\n\
     Also remove user data (print queue, settings, logs)?" \
    /SD IDYES IDNO keepdata
  RMDir /r "$INSTDIR"
  FileWrite $9 "full rmdir done$\r$\n"
  Goto regcleanup
keepdata:
  RMDir "$INSTDIR"
  FileWrite $9 "keepdata rmdir done$\r$\n"

regcleanup:
  ; 快捷方式与自启
  Delete "$DESKTOP\PipPrinter.lnk"
  RMDir /r "$SMPROGRAMS\PipPrinter"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PipPrinter"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\PipPrinter"
  DeleteRegKey HKCU "Software\PipPrinter"
  FileWrite $9 "reg cleanup done$\r$\n"
  FileClose $9
SectionEnd

; ---- 组件描述（须置于 Section 定义之后）----
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_CORE} $(DESC_CORE)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_SHORTCUTS} $(DESC_SHORTCUTS)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} $(DESC_DESKTOP)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTORUN} $(DESC_AUTORUN)
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_OFFICE} $(DESC_OFFICE)
!insertmacro MUI_FUNCTION_DESCRIPTION_END
