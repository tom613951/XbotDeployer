# XbotDeployer

> **使用建议：建议开通影刀创业版 / 企业版以获得完整的云端应用同步、协同开发及自定义指令集生态支持。**

XbotDeployer 是一款用于在不同账号间打包、迁移和部署影刀（ShadowBot）本地应用的桌面工具。支持读取本地应用与元数据，并将其打包同步至目标接收账号的云端空间。

本项目基于 C# 与 .NET 10 WPF UI 进行全新重构，采用原生 Windows 11 Fluent Design 风格与 Mica 材质，体积轻量，启动秒开。

## 功能特性

- **多盘符本地应用检测与自定义路径**：全面扫描系统注册表（涵盖 HKLM 与 HKCU 64/32位全视图 InstallPath 及卸载项）、运行中进程与全盘常见路径，自动解析多格式用户 Profile（支持纯数字 UID 与 UUID 目录）及其本地应用元数据；支持界面手动指定任意影刀安装目录或数据目录。
- **回归原生应用契约与子流程完整性**：严格对齐影刀原生应用目录规范与云端同步协议，避免错误嵌套导致接收端扩展指令错位；流程文件与元数据 100% 原样无损传输，杜绝流程名污染与反序列化异常；依赖指令集（`internaldependencies`）纯透传云端由接收端官方管道无感自动安装，彻底根治非默认盘符安装环境下的子流程空白缺陷。
- **运行环境版本对齐与源码保护**：优先按应用 `venv\pyvenv.cfg` 声明版本匹配影刀内置解释器；无法确定匹配解释器时严格保留原始 Python 源码，杜绝魔数冲突。
- **应用右键快捷管理与双向同步**：
  - **重命名应用**：同步修改本地 `package.json` 与当前用户的 SQLite 数据库（`user.db3`），影刀客户端实时生效且不会被云端覆盖。
  - **双模式安全删除**：默认提供“仅清除本地缓存与文件”（安全防误删，保留云端空间及同步状态）与“彻底删除（本地与云端同步销毁）”（联动官方网关注销云端登记并彻底清理本地记录）。
  - **定位与详情**：支持一键在资源管理器中打开应用根目录及查看完整元数据。
- **应用打包与规范化导出**：将本地应用打包为标准的 Zip 备份格式，强制采用标准正斜杠路径分隔符，支持选择编译为 Python 字节码 (.pyc) 以保护源代码。
- **跨账号云端部署**：通过 OAuth 认证与云存储接口，直接将本地应用同步推送至指定的接收方账号云端空间。
- **常用联系人管理**：内置本地 SQLite 存储，支持持久化保存接收方账号密码，且数据独立存储在应用数据漫游目录（%APPDATA%），保持程序运行目录整洁。
- **界面与交互体验**：基于 WPF UI 构建现代化 Fluent 界面，支持系统深浅色主题自适应、无抖动流畅交互及实时高对比度终端日志输出。

## 构建与发布

环境要求：.NET 10 SDK。

构建项目：

```powershell
dotnet build .\XbotDeployer\XbotDeployer.csproj -c Release
```

发布 Windows x64 依赖框架单文件版本：

```powershell
dotnet publish .\XbotDeployer\XbotDeployer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\dist
```

发布 Windows x64 独立自包含版本（无需预装 .NET 运行时）：

```powershell
dotnet publish .\XbotDeployer\XbotDeployer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\selfcontained
```

## 使用说明

1. 运行 `XbotDeployer.exe`。
2. 顶部工具栏确认当前选中的影刀用户（若影刀安装在非 C 盘，可通过“自定义目录”手动指定路径），点击“刷新列表”可重新检测账号与应用。
3. 在左侧应用列表中勾选需要导出的备份或需要迁移的应用，亦可右键条目进行重命名、缓存清理或彻底删除。
4. 在右侧配置接收方账号与密码，可保存至常用联系人以备后续使用。
5. 点击“开始一键迁移选中应用”，在右侧终端中实时查看打包、上传与云端校验日志。
