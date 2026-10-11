# XbotDeployer

XbotDeployer 是一款用于在不同账号间打包、迁移和部署影刀（ShadowBot）本地应用的桌面工具。支持读取本地应用与元数据，并将其打包同步至目标接收账号的云端空间。

本项目基于 C# 与 .NET 10 WPF UI 进行全新重构，采用原生 Windows 11 Fluent Design 风格与 Mica 材质，体积更轻量，启动秒开。

## 功能特性

- **本地应用检测**：自动扫描影刀用户数据路径，解析所有本地应用及其元数据（UUID、大小、流数量、修改时间）。
- **右键快捷管理**：列表条目支持右键快捷菜单，可实时对本地应用进行**重命名**与**安全删除**，并双向同步更新至影刀本地应用元数据与 SQLite 数据库（user.db3），支持一键定位打开所在文件夹与查看应用详情。
- **子流程与全量依赖保障**：内置增强型流程积木块修复机制，全面支持密文流程（`*.flow.json.enc`）与明文流程（`*.flow.json`）的子流程调用（`process.run`）名称自动对齐，深度兼容递归积木容器与 `xbot_extensions` 扩展组件，根治迁移后子流程为空的缺陷。
- **应用打包与导出**：将本地应用打包为标准的 Zip 备份格式，支持选择编译为 Python 字节码 (.pyc) 以保护源代码。
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
2. 在左侧应用列表中勾选需要导出的备份或需要迁移的应用。
3. 在右侧配置接收方账号与密码，可保存至常用联系人以备后续使用。
4. 点击“开始一键迁移选中应用”，在右侧终端中实时查看打包、上传与云端校验日志。

---

建议开通创业版/企业版
