# Xbot Deployer

Xbot Deployer is an open-source tool for packaging, migrating, and deploying ShadowBot (Yingdao RPA) local applications across different accounts. It provides both a graphical user interface (GUI) and a command-line interface (CLI) to seamlessly back up local apps and deploy them directly to a target receiver account's cloud space.

## Features

- Scan and List Local Apps: Automatically detects ShadowBot user data paths and lists all local applications with their metadata (UUID, size, modified time).
- App Packaging & Export: Package local xbot_robot applications into standard zip formats. Supports compiling Python source files into encrypted bytecodes (.pyc) for code protection.
- Cross-Account Cloud Deployment: Push a local app directly to another user's ShadowBot account via OAuth authentication and cloud storage API integration.
- Contacts Management: Built-in local SQLite database to save and manage receiver credentials securely (uses simple local obfuscation).
- Hybrid Interface: Can be launched as a Qt-based graphical interface for easy point-and-click operations, or via command-line arguments for scripting and automation.

## Command Line Interface

```bash
usage: main.py [-h] {list,export,deploy} ...

positional arguments:
  {list,export,deploy}
    list                List all local ShadowBot apps
    export              Export a specific app to a Zip package
    deploy              Migrate an app across accounts

options:
  -h, --help            show this help message and exit
```

## GUI Application

Simply run the executable or `python main.py` without arguments to launch the PyQt6 graphical interface.

## Requirements

- Python 3.12+
- PyQt6
- requests
- pycryptodome

## Build from Source

You can compile the application into a standalone executable using Nuitka:

```bash
python -m nuitka --standalone --onefile --enable-plugin=pyqt6 --windows-console-mode=attach --output-filename="XbotDeployer.exe" --output-dir="build_out" --remove-output main.py
```
