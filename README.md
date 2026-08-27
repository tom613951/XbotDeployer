# Xbot Deployer

Xbot Deployer 是一款用于在不同账号间打包、迁移和部署影刀（ShadowBot）本地应用的开源工具。本项目同时提供图形化界面（GUI）与命令行界面（CLI），能够无缝备份本地应用并将其直接推送至目标接收账号的云端空间中。

## 功能特性

- 扫描与列出本地应用：自动检测影刀的用户数据路径，并列出所有本地应用及其元数据（UUID、大小、修改时间）。
- 应用打包与导出：将本地 xbot_robot 应用打包为标准的 zip 格式。支持将 Python 源码文件编译为加密的字节码 (.pyc)，以提供代码保护。
- 跨账号云端部署：通过 OAuth 认证与云存储 API 集成，将本地应用直接推送至另一个用户的影刀账号。
- 联系人管理：内置本地 SQLite 数据库，可安全保存和管理接收方的账号密码（采用本地混淆加密保护）。
- 混合界面：可以作为基于 PyQt6 的图形化界面启动，方便鼠标点击操作；也可以通过命令行参数调用，满足脚本化和自动化的需求。

## 命令行操作指南

```bash
usage: main.py [-h] {list,export,deploy} ...

positional arguments:
  {list,export,deploy}
    list                列出本地所有影刀应用
    export              将指定应用导出为 Zip 压缩包
    deploy              跨账号一键迁移应用

options:
  -h, --help            显示此帮助信息并退出
```

## 图形化界面

直接运行编译后的可执行文件，或者在不带任何参数的情况下运行 `python main.py`，即可启动 PyQt6 图形界面。

## 环境要求

- Python 3.12+
- PyQt6
- requests
- pycryptodome

## 从源码构建打包

您可以使用 Nuitka 将此应用编译为独立的单文件可执行程序：

```bash
python -m nuitka --standalone --onefile --enable-plugin=pyqt6 --windows-console-mode=attach --output-filename="XbotDeployer.exe" --output-dir="build_out" --remove-output main.py
```
