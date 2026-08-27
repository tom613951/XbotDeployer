"""
ShadowBot 应用打包与代码处理模块
"""
import os
import shutil
import hashlib
import json
import zipfile
import py_compile
import tempfile
from typing import Tuple, Dict, Any, Optional


def calculate_md5(file_path: str) -> str:
    """计算文件的 MD5 摘要 (小写 32 位 hex)"""
    hasher = hashlib.md5()
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def compile_py_to_pyc(src_py: str, dst_pyc: str) -> bool:
    """编译单个 Python 源码文件为 .pyc 字节码"""
    py_compile.compile(src_py, cfile=dst_pyc, doraise=True)
    return True


def build_app_package(
    robot_dir: str,
    new_app_name: Optional[str] = None,
    new_uuid: Optional[str] = None,
    encrypt_python: bool = False,
    output_dir: Optional[str] = None
) -> Tuple[str, str, Dict[str, Any], str]:
    """
    打包影刀应用为标准 package.bot 结构并生成 package.json

    :param robot_dir: 原始应用的 xbot_robot 目录
    :param new_app_name: 迁移后的新应用名称 (若为空则保持原名)
    :param new_uuid: 迁移后的新应用 UUID (若为空则生成全新 UUID)
    :param encrypt_python: 是否将 Python 代码编译为字节码以保护源码
    :param output_dir: zip 文件输出路径 (若为空则使用临时目录)
    :return: (zip_file_path, package_md5, updated_package_json, pkg_file_path)
    """
    if not os.path.exists(robot_dir):
        raise FileNotFoundError(f"应用目录不存在: {robot_dir}")

    # 创建临时工作区
    work_temp_dir = tempfile.mkdtemp(prefix="xbot_pack_")
    stage_dir = os.path.join(work_temp_dir, "xbot_robot")

    try:
        # 复制所有文件到工作区，忽略 __pycache__、.git 等
        def ignore_patterns(path, names):
            ignored = set()
            for n in names:
                if n in [".git", ".dev", ".svn", "__pycache__", ".vscode", ".idea", "venv"]:
                    ignored.add(n)
                elif n.endswith(".pyc") and not encrypt_python:
                    ignored.add(n)
            return ignored

        shutil.copytree(robot_dir, stage_dir, ignore=ignore_patterns)

        # 读取并更新 package.json
        pkg_file = os.path.join(stage_dir, "package.json")
        pkg_data = {}
        if os.path.exists(pkg_file):
            with open(pkg_file, "r", encoding="utf-8") as f:
                pkg_data = json.load(f)

        if new_app_name:
            pkg_data["name"] = new_app_name

        if new_uuid:
            pkg_data["uuid"] = new_uuid

        pkg_data["version"] = "1"

        if encrypt_python:
            pkg_data["encrypt_bot"] = True

            # 遍历 stage_dir 中的所有 .py 文件编译为 .pyc 并删除源 .py
            for root, _, files in os.walk(stage_dir):
                for f in files:
                    if f.endswith(".py") and f != "__init__.py":
                        py_path = os.path.join(root, f)
                        pyc_path = os.path.join(root, f[:-3] + ".pyc")
                        if compile_py_to_pyc(py_path, pyc_path):
                            os.remove(py_path)

        # 写回 package.json
        with open(pkg_file, "w", encoding="utf-8") as f:
            json.dump(pkg_data, f, ensure_ascii=False, indent=2)

        # 确定 zip 输出路径
        if not output_dir:
            out_target_dir = tempfile.mkdtemp(prefix="xbot_out_")
        else:
            out_target_dir = output_dir
            os.makedirs(out_target_dir, exist_ok=True)

        zip_path = os.path.join(out_target_dir, "package.bot")
        if os.path.exists(zip_path):
            os.remove(zip_path)

        out_json_path = os.path.join(out_target_dir, "package.json")
        with open(out_json_path, "w", encoding="utf-8") as f:
            json.dump(pkg_data, f, ensure_ascii=False, indent=2)

        # 打包 stage_dir 内的所有内容 (顶层即为 package.json、main.py 等)
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            for root, dirs, files in os.walk(stage_dir):
                for f in files:
                    full_fp = os.path.join(root, f)
                    rel_fp = os.path.relpath(full_fp, stage_dir)
                    zf.write(full_fp, arcname=rel_fp)

        # 计算 MD5
        pkg_md5 = calculate_md5(zip_path)

        return zip_path, pkg_md5, pkg_data, out_json_path

    finally:
        # 清理中间构建临时目录
        shutil.rmtree(work_temp_dir, ignore_errors=True)
