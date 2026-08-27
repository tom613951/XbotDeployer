"""
Xbot Deployer 影刀应用一键迁移与部署工具
主入口文件
"""
import sys
import os

# 确保项目根目录在 sys.path 中
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def main():
    if len(sys.argv) > 1:
        from cli import run_cli
        run_cli()
    else:
        from PyQt6.QtWidgets import QApplication
        from PyQt6.QtGui import QFont
        from gui.main_window import MainWindow

        app = QApplication(sys.argv)
        app.setFont(QFont("Microsoft YaHei", 9))

        window = MainWindow()
        window.show()
        sys.exit(app.exec())


if __name__ == "__main__":
    main()
