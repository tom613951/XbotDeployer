"""
本地 ShadowBot 环境与应用扫描模块
"""
import os
import json
from typing import List, Dict, Any, Optional
from datetime import datetime


def get_default_shadowbot_dir() -> str:
    """获取本地 ShadowBot 默认安装数据目录"""
    local_app_data = os.environ.get("LOCALAPPDATA", "")
    return os.path.join(local_app_data, "ShadowBot")


def get_shadowbot_users(shadowbot_dir: Optional[str] = None) -> List[Dict[str, Any]]:
    """
    扫描本地所有 ShadowBot 用户目录
    :return: List of dict: [{'user_id': '...', 'path': '...', 'app_count': N}, ...]
    """
    if not shadowbot_dir:
        shadowbot_dir = get_default_shadowbot_dir()

    users_dir = os.path.join(shadowbot_dir, "users")
    if not os.path.exists(users_dir):
        return []

    users = []
    for item in os.listdir(users_dir):
        upath = os.path.join(users_dir, item)
        if os.path.isdir(upath) and item != "Assistant":
            apps_dir = os.path.join(upath, "apps")
            app_count = 0
            if os.path.exists(apps_dir):
                app_count = len([d for d in os.listdir(apps_dir) if os.path.isdir(os.path.join(apps_dir, d))])
            users.append({
                "user_id": item,
                "path": upath,
                "apps_path": apps_dir,
                "app_count": app_count
            })
    return users



def calculate_dir_size(path: str) -> int:
    """计算文件夹总大小（字节）"""
    total = 0
    try:
        for root, _, files in os.walk(path):
            for f in files:
                fp = os.path.join(root, f)
                if os.path.exists(fp):
                    total += os.path.getsize(fp)
    except Exception:
        pass
    return total


def format_size(bytes_size: int) -> str:
    """格式化文件大小"""
    for unit in ['B', 'KB', 'MB', 'GB']:
        if bytes_size < 1024.0:
            return f"{bytes_size:.1f} {unit}"
        bytes_size /= 1024.0
    return f"{bytes_size:.1f} TB"


def scan_local_apps(user_path: Optional[str] = None, shadowbot_dir: Optional[str] = None) -> List[Dict[str, Any]]:
    """
    扫描指定用户或所有用户目录下的本地应用
    :return: 应用元数据列表
    """
    if not shadowbot_dir:
        shadowbot_dir = get_default_shadowbot_dir()

    target_user_paths = []
    if user_path and os.path.exists(user_path):
        target_user_paths.append(user_path)
    else:
        users = get_shadowbot_users(shadowbot_dir)
        target_user_paths = [u["path"] for u in users]

    apps_list = []
    for upath in target_user_paths:
        apps_dir = os.path.join(upath, "apps")
        if not os.path.exists(apps_dir):
            continue

        user_id = os.path.basename(upath)

        for app_uuid in os.listdir(apps_dir):
            app_dir = os.path.join(apps_dir, app_uuid)
            if not os.path.isdir(app_dir):
                continue
            # 跳过 ShadowBot 临时副本目录
            if app_uuid.endswith("_temp"):
                continue

            robot_dir = os.path.join(app_dir, "xbot_robot")
            pkg_file = os.path.join(robot_dir, "package.json")

            # 如果没有 xbot_robot，检查是否在根目录
            if not os.path.exists(pkg_file):
                robot_dir = app_dir
                pkg_file = os.path.join(robot_dir, "package.json")

            if not os.path.exists(pkg_file):
                continue

            try:
                with open(pkg_file, "r", encoding="utf-8") as f:
                    pkg_data = json.load(f)
            except Exception:
                continue  # 跳过无法读取或损坏的 package.json

            app_name = pkg_data.get("name") or app_uuid
            mtime = os.path.getmtime(pkg_file)
            mtime_str = datetime.fromtimestamp(mtime).strftime("%Y-%m-%d %H:%M:%S")
            app_size = calculate_dir_size(robot_dir)

            flows = pkg_data.get("flows", [])
            flow_count = len(flows)

            apps_list.append({
                "uuid": app_uuid,
                "name": app_name,
                "user_id": user_id,
                "version": pkg_data.get("version", 1),
                "description": pkg_data.get("description", ""),
                "app_dir": app_dir,
                "robot_dir": robot_dir,
                "package_file": pkg_file,
                "package_data": pkg_data,
                "flow_count": flow_count,
                "mtime": mtime,
                "mtime_str": mtime_str,
                "size_bytes": app_size,
                "size_str": format_size(app_size),
                "is_encrypted": pkg_data.get("encrypt_bot", False)
            })

    # 按修改时间从新到旧排序
    apps_list.sort(key=lambda x: x["mtime"], reverse=True)
    return apps_list
