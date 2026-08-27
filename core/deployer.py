"""
影刀云端 API 交互与应用部署同步模块
"""
import os
import uuid
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
        version: int = 1,
        app_type: str = "app",
        is_bot: bool = False
    ) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
        """
        请求分配云存储资源与上传签名 URL
        """
        url = f"{self.api_base_url}/api/client/app/file/assignUploadUrl"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8"
        }
        payload = {
            "appId": "",
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
        file_path: str,
        progress_callback: Optional[Callable[[str], None]] = None
    ) -> Tuple[bool, str]:
        """
        将本地 package.zip 上传至云存储 OSS
        """
        if not os.path.exists(file_path):
            return False, f"本地应用包不存在: {file_path}"

        if progress_callback:
            progress_callback(f"正在上传应用包至云端 ({os.path.getsize(file_path)} 字节)...")

        try:
            with open(file_path, "rb") as f:
                file_bytes = f.read()

            headers = {}

            # OSS 通常使用 PUT 请求
            resp = self.session.put(upload_url, headers=headers, data=file_bytes, timeout=60)
            if resp.status_code in [200, 201, 204]:
                return True, "云端文件上传成功"

            # PUT 失败，尝试 POST (通常不应该运行到这里，OSS严格校验方法)
            resp_post = self.session.post(upload_url, headers=headers, data=file_bytes, timeout=60)
            if resp_post.status_code in [200, 201, 204]:
                return True, "云端文件上传成功"
            
            # 如果双双失败，主要打印 PUT 的错误（因为OSS签名的肯定是PUT）
            return False, f"上传失败 (PUT {resp.status_code}): {resp.text}"
        except Exception as e:
            return False, f"上传应用包网络异常: {e}"

    def create_develop_app(
        self,
        target_token: str,
        pkg_data: Dict[str, Any],
        package_code: str,
        package_md5: str
    ) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
        """
        在接收方账号下创建并注册新应用
        """
        url = f"{self.api_base_url}/api/client/app/develop/create"
        headers = {
            "Authorization": f"bearer {target_token}",
            "Content-Type": "application/json; charset=utf-8",
            "Xybot-Client-RequestId": str(uuid.uuid4())
        }

        payload = {
            "appName": pkg_data.get("name", "未命名应用"),
            "description": pkg_data.get("description") or "",
            "appIcon": pkg_data.get("icon") or "",
            "uiaType": pkg_data.get("uia_type", "PC"),
            "packageCode": package_code,
            "packageMd5": package_md5,
            "instruction": pkg_data.get("instruction") or "",
            "internalDependencies": pkg_data.get("internaldependencies") or [],
            "externalDependencies": pkg_data.get("external_dependencies") or [],
            "ipaasDependencies": pkg_data.get("ipaasDependencies") or [],
            "customItems": pkg_data.get("customItems") or {},
            "videoUrl": pkg_data.get("videoUrl") or "",
            "gifUrl": pkg_data.get("gifUrl") or "",
            "imageUrl": pkg_data.get("imageUrl") or "",
            "imageName": pkg_data.get("imageName") or "",
            "appFlowParamList": pkg_data.get("appFlowParamList") or [],
            "statistics": pkg_data.get("statistics") or {},
            "elementLibraryCodes": pkg_data.get("elementLibraryCodes") or [],
            "elementLibraryStatus": pkg_data.get("elementLibraryStatus") or 0,
            "groupId": pkg_data.get("groupId") or None,
            "enableViewSource": pkg_data.get("enableViewSource", True)
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

    def deploy_single_app(
        self,
        target_token: str,
        robot_dir: str,
        new_app_name: Optional[str] = None,
        encrypt_python: bool = False,
        log_callback: Optional[Callable[[str], None]] = None
    ) -> Tuple[bool, str]:
        """
        完整迁移执行流程（一键打包 -> 上传 -> 接收端创建）
        """
        def log(msg: str):
            if log_callback:
                log_callback(msg)

        zip_path = None
        try:
            log(f"📦 开始打包本地应用: {new_app_name or os.path.basename(robot_dir)}...")
            zip_path, pkg_md5, pkg_data = build_app_package(
                robot_dir=robot_dir,
                new_app_name=new_app_name,
                encrypt_python=encrypt_python
            )
            log(f"✅ 打包完成 | 包大小: {os.path.getsize(zip_path)} 字节 | MD5: {pkg_md5}")

            # 1. 申请云存储资源
            log("🌐 正在向影刀云端申请云存储资源...")
            ok, msg, res_data = self.assign_upload_url(
                target_token=target_token,
                version=pkg_data.get("version", 1)
            )
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            upload_url = res_data.get("uploadUrl")
            file_key_md5 = res_data.get("fileKeyMd5") or res_data.get("uploadKey") or pkg_md5
            if not upload_url:
                err = "❌ 云存储返回信息缺少 uploadUrl"
                log(err)
                return False, err

            log("✅ 云存储资源分配成功，正在上传文件...")

            # 2. 上传文件
            ok, msg = self.upload_file_to_cloud(upload_url, zip_path, progress_callback=log)
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            log("✅ 应用包上传云端成功，正在接收方账号注册应用...")

            # 3. 注册创建应用
            ok, msg, create_res = self.create_develop_app(
                target_token=target_token,
                pkg_data=pkg_data,
                package_code=file_key_md5,
                package_md5=pkg_md5
            )
            if not ok:
                log(f"❌ {msg}")
                return False, msg

            success_msg = f"🎉 迁移成功！应用【{pkg_data.get('name')}】已推送到目标账号，通知接收方登录影刀客户端双击同步即可！"
            log(success_msg)
            return True, success_msg

        except Exception as e:
            err = f"❌ 应用打包或部署失败: {e}"
            log(err)
            return False, err

        finally:
            # 清理生成的临时 zip 文件
            if zip_path and os.path.exists(zip_path):
                try:
                    os.remove(zip_path)
                except Exception:
                    pass

