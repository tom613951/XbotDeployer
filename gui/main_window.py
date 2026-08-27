"""
Xbot Deployer 主界面 (PyQt6 现代化界面)
"""
import os
import shutil
from datetime import datetime
from typing import List, Dict, Any, Optional
from html import escape as html_escape

from PyQt6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, QLabel,
    QLineEdit, QPushButton, QTableWidget, QTableWidgetItem,
    QCheckBox, QComboBox, QTextEdit, QProgressBar, QSplitter,
    QFileDialog, QMessageBox, QHeaderView, QGroupBox, QStatusBar
)
from PyQt6.QtCore import Qt, QThread, pyqtSignal
from PyQt6.QtGui import QFont
from PyQt6.QtGui import QFont, QPixmap, QIcon

from core.scanner import scan_local_apps, get_shadowbot_users, get_default_shadowbot_dir
from core.auth import login_shadowbot
from core.deployer import ShadowBotDeployer
from core.packager import build_app_package
from core.contacts import ContactsDB
from gui.contacts_dialog import ContactsManagerDialog
from gui.icon import ICON_B64
import base64


class MigrationWorker(QThread):
    """后台迁移线程"""
    log_signal = pyqtSignal(str, str)         # (msg, level)
    progress_signal = pyqtSignal(int, int)    # (current, total)
    finished_signal = pyqtSignal(bool, str)   # (success, summary_msg)

    def __init__(
        self,
        deployer: ShadowBotDeployer,
        target_username: str,
        target_password: str,
        selected_apps: List[Dict[str, Any]],
        add_suffix: bool,
        encrypt_python: bool
    ):
        super().__init__()
        self.deployer = deployer
        self.target_username = target_username
        self.target_password = target_password
        self.selected_apps = selected_apps
        self.add_suffix = add_suffix
        self.encrypt_python = encrypt_python

    def run(self):
        self.log_signal.emit(f"🔑 正在登录接收方账号 [{self.target_username}]...", "INFO")
        ok, msg, token_res = login_shadowbot(self.target_username, self.target_password)
        if not ok or not token_res:
            self.log_signal.emit(f"❌ 接收方账号登录失败: {msg}", "ERROR")
            self.finished_signal.emit(False, f"登录失败: {msg}")
            return

        target_token = token_res.get("access_token")
        user_uuid = token_res.get("userUuid", "未知")
        self.log_signal.emit(f"✅ 接收方账号登录成功！(UserUUID: {user_uuid})", "SUCCESS")

        total = len(self.selected_apps)
        success_count = 0

        for i, app in enumerate(self.selected_apps, 1):
            cur_name = app["name"]
            if self.add_suffix:
                now_str = datetime.now().strftime("%Y年%m月%d日 %H时%M分%S秒")
                migrated_name = f"{cur_name}_云迁_接收于{now_str}"
            else:
                migrated_name = cur_name

            self.log_signal.emit(f"\n--- 正在处理 [{i}/{total}] {cur_name} ---", "INFO")
            ok, res_msg = self.deployer.deploy_single_app(
                target_token=target_token,
                robot_dir=app["robot_dir"],
                new_app_name=migrated_name,
                encrypt_python=self.encrypt_python,
                log_callback=lambda m: self.log_signal.emit(m, "INFO")
            )

            if ok:
                success_count += 1
            else:
                self.log_signal.emit(f"❌ 迁移失败: {res_msg}", "ERROR")

            self.progress_signal.emit(i, total)

        summary = f"迁移任务结束: 共 {total} 个应用，成功 {success_count} 个，失败 {total - success_count} 个"
        self.log_signal.emit(f"\n{summary}", "SUCCESS" if success_count == total else "WARN")
        self.finished_signal.emit(True, summary)


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("XbotDeployer")
        self.resize(1100, 750)
        
        pixmap = QPixmap()
        pixmap.loadFromData(base64.b64decode(ICON_B64))
        self.setWindowIcon(QIcon(pixmap))

        self.db = ContactsDB()
        self.deployer = ShadowBotDeployer()
        self.all_apps: List[Dict[str, Any]] = []
        self.filtered_apps: List[Dict[str, Any]] = []
        self.worker: Optional[MigrationWorker] = None

        self._build_ui()
        self._load_users_and_apps()
        self._load_contacts()

    def _build_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        main_layout = QVBoxLayout(central_widget)
        main_layout.setContentsMargins(12, 10, 12, 10)
        main_layout.setSpacing(8)

        # 1. 顶部控制栏
        top_bar = QHBoxLayout()
        top_bar.addWidget(QLabel("当前影刀用户:"))

        self.cmb_users = QComboBox()
        self.cmb_users.setMinimumWidth(220)
        self.cmb_users.currentIndexChanged.connect(self._on_user_changed)
        top_bar.addWidget(self.cmb_users)

        btn_refresh = QPushButton("🔄 刷新应用列表")
        btn_refresh.clicked.connect(self._on_refresh_clicked)
        top_bar.addWidget(btn_refresh)

        btn_clean = QPushButton("🧹 清理临时缓存")
        btn_clean.clicked.connect(self._clean_temp_cache)
        top_bar.addWidget(btn_clean)

        top_bar.addStretch()

        self.lbl_app_summary = QLabel("共 0 个应用")
        self.lbl_app_summary.setStyleSheet("color: #666666;")
        top_bar.addWidget(self.lbl_app_summary)

        main_layout.addLayout(top_bar)

        # 2. 中间分割区 (左侧应用列表，右侧设置与日志)
        splitter = QSplitter(Qt.Orientation.Horizontal)

        # --- 左侧：应用选择与过滤 ---
        left_widget = QWidget()
        left_layout = QVBoxLayout(left_widget)
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(6)

        left_group = QGroupBox("本地应用列表")
        group_inner = QVBoxLayout(left_group)

        search_layout = QHBoxLayout()
        search_layout.addWidget(QLabel("🔍 搜索:"))
        self.ent_search = QLineEdit()
        self.ent_search.setPlaceholderText("输入应用名称或 UUID 快速过滤...")
        self.ent_search.textChanged.connect(self._filter_apps)
        search_layout.addWidget(self.ent_search)
        group_inner.addLayout(search_layout)

        # 表格
        self.table_apps = QTableWidget()
        self.table_apps.setColumnCount(5)
        self.table_apps.setHorizontalHeaderLabels(["应用名称 (双击查看)", "修改时间", "大小", "流数量", "UUID"])
        self.table_apps.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.table_apps.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        self.table_apps.horizontalHeader().setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        self.table_apps.horizontalHeader().setSectionResizeMode(3, QHeaderView.ResizeMode.ResizeToContents)
        self.table_apps.horizontalHeader().setSectionResizeMode(4, QHeaderView.ResizeMode.ResizeToContents)
        self.table_apps.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table_apps.setSelectionMode(QTableWidget.SelectionMode.ExtendedSelection)
        self.table_apps.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table_apps.doubleClicked.connect(self._on_app_double_click)
        group_inner.addWidget(self.table_apps)

        # 左侧底部操作栏
        btn_row_left = QHBoxLayout()
        btn_select_all = QPushButton("全选")
        btn_select_all.clicked.connect(self._select_all)
        btn_row_left.addWidget(btn_select_all)

        btn_invert = QPushButton("反选")
        btn_invert.clicked.connect(self._invert_selection)
        btn_row_left.addWidget(btn_invert)

        btn_row_left.addStretch()

        btn_export = QPushButton("📦 导出选中应用为 Zip 备份")
        btn_export.clicked.connect(self._export_selected_to_zip)
        btn_row_left.addWidget(btn_export)

        group_inner.addLayout(btn_row_left)
        left_layout.addWidget(left_group)
        splitter.addWidget(left_widget)

        # --- 右侧：目标设置与操作日志 ---
        right_widget = QWidget()
        right_layout = QVBoxLayout(right_widget)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.setSpacing(8)

        # 接收人设置
        target_group = QGroupBox("目标接收方账号")
        layout_tgt = QVBoxLayout(target_group)

        row_c = QHBoxLayout()
        row_c.addWidget(QLabel("常用联系人:"))
        self.cmb_contacts = QComboBox()
        self.cmb_contacts.currentIndexChanged.connect(self._on_contact_selected)
        row_c.addWidget(self.cmb_contacts, stretch=1)
        btn_mgr_contacts = QPushButton("管理")
        btn_mgr_contacts.setFixedWidth(60)
        btn_mgr_contacts.clicked.connect(self._open_contacts_manager)
        row_c.addWidget(btn_mgr_contacts)
        layout_tgt.addLayout(row_c)

        row_u = QHBoxLayout()
        row_u.addWidget(QLabel("接收人账号:"))
        self.ent_target_user = QLineEdit()
        self.ent_target_user.setPlaceholderText("手机号 / 用户名")
        row_u.addWidget(self.ent_target_user)
        layout_tgt.addLayout(row_u)

        row_p = QHBoxLayout()
        row_p.addWidget(QLabel("接收人密码:"))
        self.ent_target_pwd = QLineEdit()
        self.ent_target_pwd.setEchoMode(QLineEdit.EchoMode.Password)
        self.ent_target_pwd.setPlaceholderText("影刀登录密码")
        row_p.addWidget(self.ent_target_pwd)
        layout_tgt.addLayout(row_p)

        self.chk_remember = QCheckBox("保存该接收方账号用于下次快速填写")
        self.chk_remember.setChecked(True)
        layout_tgt.addWidget(self.chk_remember)

        right_layout.addWidget(target_group)

        # 迁移选项
        opt_group = QGroupBox("迁移与打包选项")
        layout_opt = QVBoxLayout(opt_group)
        self.chk_suffix = QCheckBox("添加接收时间戳后缀 (防同名覆盖)")
        self.chk_suffix.setChecked(True)
        layout_opt.addWidget(self.chk_suffix)

        self.chk_encrypt_py = QCheckBox("迁移时编译加密 Python 源码 (.py -> .pyc)")
        self.chk_encrypt_py.setChecked(False)
        layout_opt.addWidget(self.chk_encrypt_py)
        right_layout.addWidget(opt_group)

        # 迁移按钮与进度条
        self.btn_migrate = QPushButton("🚀 开始一键迁移选中应用")
        self.btn_migrate.setStyleSheet("""
            QPushButton {
                background-color: #1890ff;
                color: white;
                font-size: 14px;
                font-weight: bold;
                padding: 10px;
                border-radius: 4px;
            }
            QPushButton:hover {
                background-color: #40a9ff;
            }
            QPushButton:disabled {
                background-color: #bfbfbf;
            }
        """)
        self.btn_migrate.clicked.connect(self._start_migration)
        right_layout.addWidget(self.btn_migrate)

        self.progress_bar = QProgressBar()
        self.progress_bar.setValue(0)
        self.progress_bar.setTextVisible(True)
        right_layout.addWidget(self.progress_bar)

        # 日志输出
        log_group = QGroupBox("迁移日志与输出")
        layout_log = QVBoxLayout(log_group)
        self.txt_log = QTextEdit()
        self.txt_log.setReadOnly(True)
        self.txt_log.setFont(QFont("Consolas", 9))
        layout_log.addWidget(self.txt_log)
        right_layout.addWidget(log_group, stretch=1)

        splitter.addWidget(right_widget)
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 2)

        main_layout.addWidget(splitter, stretch=1)

        # 3. 状态栏
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)
        self.status_bar.showMessage("就绪 | Xbot Deployer")

    def log(self, message: str, level: str = "INFO"):
        """向日志窗口追加信息"""
        now_str = datetime.now().strftime("%H:%M:%S")
        color_map = {
            "INFO": "#333333",
            "SUCCESS": "#2e7d32",
            "ERROR": "#d32f2f",
            "WARN": "#ed6c02"
        }
        color = color_map.get(level, "#333333")
        safe_msg = html_escape(message).replace("\n", "<br>")
        html = f'<span style="color: {color};">[{now_str}] {safe_msg}</span><br>'
        self.txt_log.insertHtml(html)
        self.txt_log.ensureCursorVisible()

    def _on_refresh_clicked(self):
        self.log("🔄 正在重新扫描用户与应用列表...")
        self._load_users_and_apps(preserve_selection=True)

    def _load_users_and_apps(self, preserve_selection: bool = False):
        current_uid = None
        if preserve_selection and hasattr(self, "users_data") and self.cmb_users.currentIndex() >= 0:
            if self.cmb_users.currentIndex() < len(self.users_data):
                current_uid = self.users_data[self.cmb_users.currentIndex()]["user_id"]

        users = get_shadowbot_users()
        if not users:
            self.log("⚠️ 未在默认路径检测到 ShadowBot 用户数据目录", "WARN")
            self.cmb_users.blockSignals(True)
            self.cmb_users.clear()
            self.cmb_users.addItem("未检测到用户")
            self.cmb_users.blockSignals(False)
            return

        self.users_data = users
        self.cmb_users.blockSignals(True)
        self.cmb_users.clear()
        selected_idx = 0
        for idx, u in enumerate(users):
            self.cmb_users.addItem(f"{u['user_id']} ({u['app_count']}个应用)")
            if current_uid and u["user_id"] == current_uid:
                selected_idx = idx
        self.cmb_users.setCurrentIndex(selected_idx)
        self.cmb_users.blockSignals(False)

        self._reload_apps()

    def _on_user_changed(self):
        self._reload_apps()

    def _reload_apps(self):
        idx = self.cmb_users.currentIndex()
        if idx >= 0 and hasattr(self, "users_data") and idx < len(self.users_data):
            selected_user = self.users_data[idx]
            self.all_apps = scan_local_apps(user_path=selected_user["path"])
            # 同步更新当前下拉项的显示数量，确保完全一致
            selected_user["app_count"] = len(self.all_apps)
            self.cmb_users.blockSignals(True)
            self.cmb_users.setItemText(idx, f"{selected_user['user_id']} ({len(self.all_apps)}个应用)")
            self.cmb_users.blockSignals(False)
        else:
            self.all_apps = scan_local_apps()

        self.log(f"已扫描到 {len(self.all_apps)} 个本地应用")
        self._filter_apps()

    def _filter_apps(self):
        keyword = self.ent_search.text().strip().lower()
        if not keyword:
            self.filtered_apps = list(self.all_apps)
        else:
            self.filtered_apps = [
                app for app in self.all_apps
                if keyword in app["name"].lower() or keyword in app["uuid"].lower()
            ]

        self.table_apps.setRowCount(0)
        for r_idx, app in enumerate(self.filtered_apps):
            self.table_apps.insertRow(r_idx)
            item_name = QTableWidgetItem(app["name"])
            item_name.setFlags(item_name.flags() | Qt.ItemFlag.ItemIsUserCheckable)
            item_name.setCheckState(Qt.CheckState.Unchecked)

            item_time = QTableWidgetItem(app["mtime_str"])
            item_size = QTableWidgetItem(app["size_str"])
            item_flows = QTableWidgetItem(str(app["flow_count"]))
            item_uuid = QTableWidgetItem(app["uuid"])

            # 居中对齐
            item_time.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            item_size.setTextAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
            item_flows.setTextAlignment(Qt.AlignmentFlag.AlignCenter)

            self.table_apps.setItem(r_idx, 0, item_name)
            self.table_apps.setItem(r_idx, 1, item_time)
            self.table_apps.setItem(r_idx, 2, item_size)
            self.table_apps.setItem(r_idx, 3, item_flows)
            self.table_apps.setItem(r_idx, 4, item_uuid)

        self.lbl_app_summary.setText(f"共 {len(self.filtered_apps)} / {len(self.all_apps)} 个应用")

    def _select_all(self):
        for r in range(self.table_apps.rowCount()):
            item = self.table_apps.item(r, 0)
            if item:
                item.setCheckState(Qt.CheckState.Checked)

    def _invert_selection(self):
        for r in range(self.table_apps.rowCount()):
            item = self.table_apps.item(r, 0)
            if item:
                if item.checkState() == Qt.CheckState.Checked:
                    item.setCheckState(Qt.CheckState.Unchecked)
                else:
                    item.setCheckState(Qt.CheckState.Checked)

    def _on_app_double_click(self, index):
        row = index.row()
        uuid_item = self.table_apps.item(row, 4)
        if not uuid_item:
            return
        uuid_val = uuid_item.text()
        app_item = next((a for a in self.all_apps if a["uuid"] == uuid_val), None)
        if app_item:
            msg = (
                f"应用名称: {app_item['name']}\n"
                f"UUID: {app_item['uuid']}\n"
                f"所属用户: {app_item['user_id']}\n"
                f"版本: {app_item['version']}\n"
                f"流程数量: {app_item['flow_count']}\n"
                f"文件大小: {app_item['size_str']}\n"
                f"修改时间: {app_item['mtime_str']}\n"
                f"本地路径: {app_item['app_dir']}"
            )
            QMessageBox.information(self, "应用详情", msg)

    def _load_contacts(self):
        contacts = self.db.get_all()
        self.contacts_data = contacts
        self.cmb_contacts.blockSignals(True)
        self.cmb_contacts.clear()
        self.cmb_contacts.addItem("-- 请选择或手动输入 --")
        for c in contacts:
            label = f"{c['username']}" + (f" ({c['remark']})" if c.get('remark') else "")
            self.cmb_contacts.addItem(label)
        self.cmb_contacts.blockSignals(False)

    def _on_contact_selected(self, index: int):
        if index > 0 and hasattr(self, "contacts_data") and (index - 1) < len(self.contacts_data):
            c = self.contacts_data[index - 1]
            self.ent_target_user.setText(c["username"])
            self.ent_target_pwd.setText(c["password"])

    def _open_contacts_manager(self):
        dialog = ContactsManagerDialog(self, self.db, on_update_callback=self._load_contacts)
        dialog.exec()

    def _export_selected_to_zip(self):
        selected_rows = []
        for r in range(self.table_apps.rowCount()):
            item = self.table_apps.item(r, 0)
            if item and item.checkState() == Qt.CheckState.Checked:
                selected_rows.append(r)
        if not selected_rows:
            QMessageBox.warning(self, "提示", "请先在列表中勾选要导出的应用！")
            return

        target_dir = QFileDialog.getExistingDirectory(self, "选择导出 Zip 保存目录")
        if not target_dir:
            return

        encrypt_py = self.chk_encrypt_py.isChecked()
        selected_uuids = [self.table_apps.item(r, 4).text() for r in selected_rows]
        self.log(f"开始导出 {len(selected_uuids)} 个应用至目录: {target_dir}...")

        success_cnt = 0
        for uid in selected_uuids:
            app = next((a for a in self.all_apps if a["uuid"] == uid), None)
            if not app:
                continue
            safe_name = "".join([c for c in app["name"] if c.isalnum() or c in " ._- "]).strip()
            out_name = f"{safe_name}_{app['uuid'][:8]}.zip"
            out_path = os.path.join(target_dir, out_name)
            try:
                zip_p, md5_val, _, _ = build_app_package(
                    robot_dir=app["robot_dir"],
                    encrypt_python=encrypt_py,
                    output_dir=target_dir
                )
                if os.path.exists(zip_p) and zip_p != out_path:
                    if os.path.exists(out_path):
                        os.remove(out_path)
                    shutil.move(zip_p, out_path)
                self.log(f"✅ 成功导出: {out_name} (MD5: {md5_val})", "SUCCESS")
                success_cnt += 1
            except Exception as e:
                self.log(f"❌ 导出 [{app['name']}] 失败: {e}", "ERROR")

        self.log(f"🎉 导出完成: 成功 {success_cnt}/{len(selected_uuids)} 个", "SUCCESS")
        QMessageBox.information(self, "完成", f"导出完成！成功导出 {success_cnt} 个应用包。")

    def _clean_temp_cache(self):
        import tempfile
        temp_dir = tempfile.gettempdir()
        cleaned_size = 0
        cleaned_count = 0
        try:
            for item in os.listdir(temp_dir):
                if item.startswith("xbot_pack_") or item.startswith("xbot_out_"):
                    item_path = os.path.join(temp_dir, item)
                    if os.path.isdir(item_path):
                        for root, _, files in os.walk(item_path):
                            for f in files:
                                cleaned_size += os.path.getsize(os.path.join(root, f))
                        shutil.rmtree(item_path, ignore_errors=True)
                        cleaned_count += 1
            if cleaned_count > 0:
                self.log(f"✅ 缓存清理完成，清理 {cleaned_count} 个目录，释放约 {cleaned_size / 1024 / 1024:.2f} MB", "SUCCESS")
                QMessageBox.information(self, "完成", "本地临时缓存清理完成！")
            else:
                QMessageBox.information(self, "提示", "未发现可清理的缓存目录")
        except Exception as e:
            self.log(f"❌ 清理缓存失败: {e}", "ERROR")

    def _start_migration(self):
        selected_rows = []
        for r in range(self.table_apps.rowCount()):
            item = self.table_apps.item(r, 0)
            if item and item.checkState() == Qt.CheckState.Checked:
                selected_rows.append(r)
        if not selected_rows:
            QMessageBox.warning(self, "提示", "请先在列表中勾选要迁移的应用！")
            return

        target_username = self.ent_target_user.text().strip()
        target_password = self.ent_target_pwd.text().strip()

        if not target_username or not target_password:
            QMessageBox.warning(self, "提示", "请输入接收方影刀账号与密码！")
            return

        # 自动保存/更新到常用联系人
        if self.chk_remember.isChecked():
            self.db.add_or_update(target_username, target_password, remark="自动保存")
            self._load_contacts()

        selected_uuids = [self.table_apps.item(r, 4).text() for r in selected_rows]
        selected_apps = [a for a in self.all_apps if a["uuid"] in selected_uuids]

        self.btn_migrate.setEnabled(False)
        self.btn_migrate.setText("⏳ 正在迁移中...")
        self.progress_bar.setRange(0, len(selected_apps))
        self.progress_bar.setValue(0)

        # 启动后台工作线程
        self.worker = MigrationWorker(
            deployer=self.deployer,
            target_username=target_username,
            target_password=target_password,
            selected_apps=selected_apps,
            add_suffix=self.chk_suffix.isChecked(),
            encrypt_python=self.chk_encrypt_py.isChecked()
        )
        self.worker.log_signal.connect(self.log)
        self.worker.progress_signal.connect(lambda cur, tot: self.progress_bar.setValue(cur))
        self.worker.finished_signal.connect(self._on_migration_finished)
        self.worker.start()

    def _on_migration_finished(self, success: bool, message: str):
        self.btn_migrate.setEnabled(True)
        self.btn_migrate.setText("开始一键迁移选中应用")
        if success:
            QMessageBox.information(self, "迁移完成", message)
        else:
            QMessageBox.critical(self, "迁移失败", message)
