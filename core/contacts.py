"""
接收人账号数据库管理模块 (SQLite)
"""
import os
import sqlite3
import base64
from typing import List, Dict, Any, Optional

LOCAL_KEY = b"xbot_deployer_local_key"

def _obfuscate(text: str) -> str:
    if not text:
        return text
    b_text = text.encode('utf-8')
    res = bytes([b ^ LOCAL_KEY[i % len(LOCAL_KEY)] for i, b in enumerate(b_text)])
    return "xbot:" + base64.b64encode(res).decode('utf-8')

def _deobfuscate(b64_text: str) -> str:
    if not b64_text or not b64_text.startswith("xbot:"):
        return b64_text
    try:
        raw_b64 = b64_text[5:]
        b_data = base64.b64decode(raw_b64.encode('utf-8'))
        res = bytes([b ^ LOCAL_KEY[i % len(LOCAL_KEY)] for i, b in enumerate(b_data)])
        return res.decode('utf-8')
    except Exception:
        return b64_text


class ContactsDB:
    """接收人本地 SQLite 数据库"""

    def __init__(self, db_path: Optional[str] = None):
        if not db_path:
            # 默认保存在当前模块上一级目录的 data 或同级
            base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            db_path = os.path.join(base_dir, "contacts.db")

        self.db_path = db_path
        self._init_db()

    def _get_connection(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self):
        """初始化接收人表结构"""
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS user (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT UNIQUE NOT NULL,
                    password TEXT NOT NULL,
                    remark TEXT,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    last_used_at TIMESTAMP
                )
            """)
            conn.commit()

    def get_all(self) -> List[Dict[str, Any]]:
        """获取所有接收人列表"""
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT id, username, password, remark, created_at, last_used_at FROM user ORDER BY id DESC")
            rows = []
            for r in cursor.fetchall():
                d = dict(r)
                d["password"] = _deobfuscate(d["password"])
                rows.append(d)
            return rows

    def add_or_update(self, username: str, password: str, remark: str = "") -> bool:
        """添加或更新接收人账号信息"""
        if not username or not password:
            return False
            
        enc_password = _obfuscate(password.strip())
        
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT INTO user (username, password, remark, last_used_at)
                VALUES (?, ?, ?, CURRENT_TIMESTAMP)
                ON CONFLICT(username) DO UPDATE SET
                    password = excluded.password,
                    remark = excluded.remark,
                    last_used_at = CURRENT_TIMESTAMP
            """, (username.strip(), enc_password, remark.strip()))
            conn.commit()
            return True

    def delete(self, username: str) -> bool:
        """删除指定接收人"""
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("DELETE FROM user WHERE username = ?", (username.strip(),))
            conn.commit()
            return cursor.rowcount > 0

    def get_by_username(self, username: str) -> Optional[Dict[str, Any]]:
        """根据用户名查询"""
        with self._get_connection() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT id, username, password, remark, created_at, last_used_at FROM user WHERE username = ?", (username.strip(),))
            row = cursor.fetchone()
            if row:
                d = dict(row)
                d["password"] = _deobfuscate(d["password"])
                return d
            return None
