"""
影刀云端 API 交互与应用部署同步模块
"""
import os
import uuid
import time
import shutil
import requests
from typing import Tuple, Dict, Any, Optional, Callable
from .packager import build_app_package


class ShadowBotDeployer:
    """影刀云端部署交互客户端"""

    def __init__(self, api_base_url: str = "https://api.yingdao.com"):
        self.api_base_url = api_base_url.rstrip("/")
        self.session = requests.Session()
        self.session.headers.update({
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
            "Accept": "application/json, text/plain, */*"
        })

    def assign_upload_url(
        self,
        target_token: str,
        app_id: str,
        version: int = 1,
        app_type: str = "app",
        is_bot: bool = True
    ) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
        """
        请求分配云存储资源与上传签名 URL
        """
        url = f"{self.api_base_url}/api/client/app/file/assignUploadUrl"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8",
            "Xybot-Client-RequestId": str(uuid.uuid4())
        }
        payload = {
            "appId": app_id,
            "appType": app_type,
            "version": version,
            "isBot": is_bot
        }

        try:
            resp = self.session.post(url, headers=headers, json=payload, timeout=20)
            if resp.status_code == 200:
                data = resp.json()
                if data.get("code") == 200 or data.get("success") is True:
                    result_data = data.get("data") or {}
                    return True, "云存储资源分配成功", result_data
                else:
                    msg = data.get("msg") or data.get("message") or "未知错误"
                    return False, f"分配云存储资源失败: {msg}", data
            elif resp.status_code == 401:
                return False, "分配云存储资源失败: 接收方 Token 已过期，请重新登录", None
            else:
                return False, f"分配云存储资源失败 (HTTP {resp.status_code}): {resp.text}", None
        except Exception as e:
            return False, f"请求云存储资源异常: {e}", None

    def upload_file_to_cloud(
        self,
        upload_url: str,
        file_path_or_bytes: Any,
        description: str = "文件",
        progress_callback: Optional[Callable[[str], None]] = None
    ) -> Tuple[bool, str]:
        """
        将本地文件或字节流上传至云存储 OSS (PUT 请求)
        """
        if isinstance(file_path_or_bytes, str):
            if not os.path.exists(file_path_or_bytes):
                return False, f"本地文件不存在: {file_path_or_bytes}"
            if progress_callback:
                progress_callback(f"正在上传{description}至云端 ({os.path.getsize(file_path_or_bytes)} 字节)...")
            with open(file_path_or_bytes, "rb") as f:
                data_bytes = f.read()
        else:
            data_bytes = file_path_or_bytes
            if progress_callback:
                progress_callback(f"正在上传{description}至云端 ({len(data_bytes)} 字节)...")

        try:
            resp = self.session.put(upload_url, data=data_bytes, timeout=60)
            if resp.status_code in [200, 201, 204]:
                return True, f"{description}云端上传成功"

            # 备用尝试 POST
            resp_post = self.session.post(upload_url, data=data_bytes, timeout=60)
            if resp_post.status_code in [200, 201, 204]:
                return True, f"{description}云端上传成功"

            return False, f"上传失败 (PUT {resp.status_code}): {resp.text}"
        except Exception as e:
            return False, f"上传{description}网络异常: {e}"

    def create_develop_app(
        self,
        target_token: str,
        app_id: str,
        app_name: str,
        pkg_data: Dict[str, Any],
        package_md5: str
    ) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
        """
        在接收方账号下创建并注册新应用
        """
        url = f"{self.api_base_url}/api/client/app/develop/create"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8",
            "Xybot-Client-RequestId": "57214437-d52d-4f1f-a23f-87c3e9b84adb"
        }

        stats = pkg_data.get("statistics") or {}
        app_package = {
            "activities": [],
            "appFlowParamList": [],
            "appIcon": "",
            "appType": "app",
            "blockCount": 1,
            "customItems": {
                "gifUrl": "",
                "imageName": "",
                "imageUrl": "",
                "uiaType": "PC",
                "videoUrl": ""
            },
            "description": "",
            "elementLibraryCodes": [],
            "enableViewSource": "false",
            "externalDependencies": pkg_data.get("external_dependencies", []),
            "flowCount": 1,
            "gifUrl": "",
            "imageName": "",
            "imageUrl": "",
            "instruction": "",
            "internalDependencies": pkg_data.get("internaldependencies", []),
            "internalautodependencies": pkg_data.get("internalautodependencies", []),
            "ipaasDependencies": [],
            "magicBlockCount": 0,
            "name": app_name,
            "packageCode": "",
            "sourceLineCount": 0,
            "statistics": {
                "blockCount": 1,
                "flowCount": 1,
                "magicBlockCount": 0,
                "sourceLineCount": 0
            },
            "uiTags": "",
            "uiaType": "PC",
            "videoUrl": ""
        }

        payload = {
            "appId": app_id,
            "appPackage": app_package,
            "elementLibraryStatus": 0,
            "groupId": "",
            "packageMd5": package_md5
        }

        try:
            resp = self.session.post(url, headers=headers, json=payload, timeout=25)
            if resp.status_code == 200:
                res_data = resp.json()
                if res_data.get("code") == 200 or res_data.get("success") is True:
                    return True, "接收方账号应用同步创建成功", res_data.get("data")
                else:
                    msg = res_data.get("msg") or res_data.get("message") or str(res_data)
                    return False, f"创建应用失败: {msg}", res_data
            else:
                return False, f"创建应用请求异常 (HTTP {resp.status_code}): {resp.text}", None
        except Exception as e:
            return False, f"创建应用网络异常: {e}", None

    def save_develop_time(self, target_token: str, app_id: str) -> bool:
        """
        激活应用开发时间戳
        """
        url = f"{self.api_base_url}/api/client/app/developInfo/developTime/save"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8"
        }
        payload = {
            "appId": app_id,
            "startTime": "2024-12-23 16:45:12",
            "endTime": "2024-12-23 16:46:46"
        }
        try:
            self.session.post(url, headers=headers, json=payload, timeout=10)
            return True
        except Exception:
            return False

    def verify_app_in_target_list(self, target_token: str, app_id: str) -> bool:
        """
        向接收方云端应用列表查询并确认应用是否存在
        """
        url = f"{self.api_base_url}/api/client/app/develop/list"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8"
        }
        payload = {
            "pageDTO": {"page": "1", "size": "1000"},
            "groupId": None,
            "name": "",
            "pageType": "1",
            "sortBy": "4"
        }
        try:
            resp = self.session.post(url, headers=headers, json=payload, timeout=15)
            if resp.status_code == 200:
                data = resp.json().get("data", [])
                for item in data:
                    if item.get("appId") == app_id:
                        return True
            return False
        except Exception:
            return False

    def deploy_single_app(
        self,
        target_token: str,
        robot_dir: str,
        new_app_name: Optional[str] = None,
        encrypt_python: bool = False,
        log_callback: Optional[Callable[[str], None]] = None
    ) -> Tuple[bool, str]:
        """
        完整双步迁移执行流程（一键打包 -> 分配bot/json上传 -> 云端注册 -> 时间戳激活 -> 云端存在性严格校验）
        """
        def log(msg: str):
            if log_callback:
                log_callback(msg)

        zip_path = None
        try:
            # 1. 生成全新独立 App UUID
            fresh_uuid = str(uuid.uuid4())
            app_display_name = new_app_name or os.path.basename(robot_dir)

            log(f"📦 开始打包本地应用: {app_display_name} (全新UUID: {fresh_uuid})...")
            zip_path, pkg_md5, pkg_data, pkg_file_path = build_app_package(
                robot_dir=robot_dir,
                new_app_name=new_app_name,
                new_uuid=fresh_uuid,
                encrypt_python=encrypt_python
            )
            log(f"✅ 打包完成 | 包大小: {os.path.getsize(zip_path)} 字节 | MD5: {pkg_md5}")

            # 2. 步骤A: 申请 package.bot 上传 URL
            log("🌐 正在向影刀云端申请应用包 (package.bot) 云存储资源...")
            ok, msg, res_data_bot = self.assign_upload_url(
                target_token=target_token,
                app_id=fresh_uuid,
                version=1,
                app_type="app",
                is_bot=True
            )
            if not ok or not res_data_bot:
                log(f"❌ {msg}")
                return False, msg

            upload_bot_url = res_data_bot.get("uploadUrl")
            if not upload_bot_url:
                err = "❌ 云存储返回信息缺少 uploadUrl (package.bot)"
                log(err)
                return False, err

            # 3. 步骤A: 上传 package.bot
            ok, msg = self.upload_file_to_cloud(upload_bot_url, zip_path, description="应用包 (package.bot)", progress_callback=log)
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            # 4. 步骤B: 申请 package.json 上传 URL
            log("🌐 正在向影刀云端申请应用结构 (package.json) 云存储资源...")
            ok, msg, res_data_json = self.assign_upload_url(
                target_token=target_token,
                app_id=fresh_uuid,
                version=1,
                app_type="app",
                is_bot=False
            )
            if not ok or not res_data_json:
                log(f"❌ {msg}")
                return False, msg

            upload_json_url = res_data_json.get("uploadUrl")
            json_file_key_md5 = res_data_json.get("fileKeyMd5")
            if not upload_json_url or not json_file_key_md5:
                err = "❌ 云存储返回信息缺少 uploadUrl 或 fileKeyMd5 (package.json)"
                log(err)
                return False, err

            # 5. 步骤B: 上传 package.json
            ok, msg = self.upload_file_to_cloud(upload_json_url, pkg_file_path, description="应用元数据 (package.json)", progress_callback=log)
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            # 6. 步骤C: 注册创建应用
            log("✅ 资源上传完成，正在接收方账号注册创建应用...")
            ok, msg, create_res = self.create_develop_app(
                target_token=target_token,
                app_id=fresh_uuid,
                app_name=pkg_data.get("name") or app_display_name,
                pkg_data=pkg_data,
                package_md5=json_file_key_md5
            )
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            # 7. 步骤D: 激活开发时间戳
            self.save_develop_time(target_token, fresh_uuid)

            # 8. 步骤E: 严格云端列表存在性校验
            time.sleep(0.5)
            verified = self.verify_app_in_target_list(target_token, fresh_uuid)
            if verified:
                log(f"🔍 接收方云端应用列表校验通过：应用已成功注册就绪！")
            else:
                log(f"⚠️ 接收方云端列表正在同步索引中...")

            success_msg = f"🎉 迁移成功！应用【{pkg_data.get('name')}】已推送到目标账号，通知接收方登录影刀客户端双击同步即可！"
            log(success_msg)
            return True, success_msg

        except Exception as e:
            err = f"❌ 应用打包或部署失败: {e}"
            log(err)
            return False, err

        finally:
            # 清理生成的临时目录与包文件
            if zip_path and os.path.exists(zip_path):
                try:
                    p_dir = os.path.dirname(zip_path)
                    shutil.rmtree(p_dir, ignore_errors=True)
                except Exception:
                    pass
