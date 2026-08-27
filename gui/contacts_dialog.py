"""
接收人管理弹窗 (PyQt6)
"""
from PyQt6.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QLabel, QLineEdit,
    QPushButton, QTableWidget, QTableWidgetItem, QHeaderView,
    QMessageBox, QGroupBox
)
from PyQt6.QtCore import Qt
from typing import Callable, Optional
from core.contacts import ContactsDB


class ContactsManagerDialog(QDialog):
    def __init__(self, parent=None, db: Optional[ContactsDB] = None, on_update_callback: Optional[Callable] = None):
        super().__init__(parent)
        self.db = db or ContactsDB()
        self.on_update_callback = on_update_callback
        self.setWindowTitle("常用接收人管理")
        self.resize(600, 440)
        self.setModal(True)

        self._build_ui()
        self._load_data()

    def _build_ui(self):
        layout = QVBoxLayout(self)
        layout.setSpacing(10)
        layout.setContentsMargins(12, 12, 12, 12)

        # 1. 表格区
        group_table = QGroupBox("已保存的接收人列表")
        layout_table = QVBoxLayout(group_table)

        self.table = QTableWidget()
        self.table.setColumnCount(3)
        self.table.setHorizontalHeaderLabels(["影刀账号", "备注 / 姓名", "添加时间"])
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.table.horizontalHeader().setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        self.table.horizontalHeader().setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QTableWidget.SelectionMode.SingleSelection)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.itemSelectionChanged.connect(self._on_row_selected)

        layout_table.addWidget(self.table)
        layout.addWidget(group_table, stretch=1)

        # 2. 编辑区
        group_edit = QGroupBox("接收人信息编辑")
        layout_edit = QVBoxLayout(group_edit)

        row1 = QHBoxLayout()
        row1.addWidget(QLabel("账号:"))
        self.ent_user = QLineEdit()
        self.ent_user.setPlaceholderText("手机号 / 账号")
        row1.addWidget(self.ent_user)

        row1.addWidget(QLabel("密码:"))
        self.ent_pwd = QLineEdit()
        self.ent_pwd.setEchoMode(QLineEdit.EchoMode.Password)
        self.ent_pwd.setPlaceholderText("接收方密码")
        row1.addWidget(self.ent_pwd)
        layout_edit.addLayout(row1)

        row2 = QHBoxLayout()
        row2.addWidget(QLabel("备注:"))
        self.ent_remark = QLineEdit()
        self.ent_remark.setPlaceholderText("如: 客户A / 测试号")
        row2.addWidget(self.ent_remark)

        btn_save = QPushButton("💾 保存/更新")
        btn_save.clicked.connect(self._save_contact)
        row2.addWidget(btn_save)

        btn_del = QPushButton("🗑️ 删除选中")
        btn_del.clicked.connect(self._delete_contact)
        row2.addWidget(btn_del)

        btn_clear = QPushButton("清空输入")
        btn_clear.clicked.connect(self._clear_inputs)
        row2.addWidget(btn_clear)

        layout_edit.addLayout(row2)
        layout.addWidget(group_edit)

    def _load_data(self):
        self.table.setRowCount(0)
        contacts = self.db.get_all()
        for r_idx, c in enumerate(contacts):
            self.table.insertRow(r_idx)
            item_user = QTableWidgetItem(str(c.get("username", "")))
            item_remark = QTableWidgetItem(str(c.get("remark", "")))
            item_time = QTableWidgetItem(str(c.get("created_at", "")))
            self.table.setItem(r_idx, 0, item_user)
            self.table.setItem(r_idx, 1, item_remark)
            self.table.setItem(r_idx, 2, item_time)

    def _on_row_selected(self):
        selected_rows = self.table.selectedIndexes()
        if not selected_rows:
            return
        row = selected_rows[0].row()
        username_item = self.table.item(row, 0)
        if not username_item:
            return
        username = username_item.text()
        user_data = self.db.get_by_username(username)
        if user_data:
            self.ent_user.setText(user_data["username"])
            self.ent_pwd.setText(user_data["password"])
            self.ent_remark.setText(user_data.get("remark") or "")

    def _save_contact(self):
        username = self.ent_user.text().strip()
        pwd = self.ent_pwd.text().strip()
        remark = self.ent_remark.text().strip()

        if not username or not pwd:
            QMessageBox.warning(self, "提示", "账号和密码不能为空！")
            return

        self.db.add_or_update(username, pwd, remark)
        self._load_data()
        self._clear_inputs()
        if self.on_update_callback:
            self.on_update_callback()
        QMessageBox.information(self, "成功", f"接收人 [{username}] 已保存！")

    def _delete_contact(self):
        selected_rows = self.table.selectedIndexes()
        if not selected_rows:
            QMessageBox.warning(self, "提示", "请先在列表中选中要删除的接收人！")
            return
        row = selected_rows[0].row()
        username = self.table.item(row, 0).text()
        reply = QMessageBox.question(
            self, "确认删除",
            f"确定要删除接收人 [{username}] 吗？",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No
        )
        if reply == QMessageBox.StandardButton.Yes:
            self.db.delete(username)
            self._load_data()
            self._clear_inputs()
            if self.on_update_callback:
                self.on_update_callback()

    def _clear_inputs(self):
        self.ent_user.clear()
        self.ent_pwd.clear()
        self.ent_remark.clear()
