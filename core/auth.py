"""
ShadowBot OAuth 认证与密码加密模块
"""
import base64
import requests
from typing import Optional, Tuple, Dict, Any
from Crypto.PublicKey import RSA
from Crypto.Cipher import PKCS1_v1_5

# 影刀官方登录 RSA 公钥 (用于对密码进行 PKCS#1 v1.5 加密)
DEFAULT_PUBLIC_KEY_B64 = (
    "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCte0XfPY9GUpQ3ZasH1kVbDhRwyRAq"
    "WSeyxj290OqFHtyiZ+5SQjrEr79mk0hcZqV03fb5oYf385E3gopSERIKxVQyGoloNeDg"
    "yLu7rHHWMPo8KPDpUBlpRpHlGMgBNzJZ2BI6p7LvGAhCoA7XRuetyTlAW6EbSXBpSu1s"
    "NGBhkQIDAQAB"
)

# 影刀官方 Client Authorization
DEFAULT_BASIC_AUTH = "Basic c25zOlQ3c3ZGY0lMNGZvUGoxajk="


def encrypt_password(password: str, pub_key_b64: str = DEFAULT_PUBLIC_KEY_B64) -> str:
    """
    使用 RSA 公钥对密码进行加密并返回 Base64 编码字符串
    """
    key_der = base64.b64decode(pub_key_b64)
    key = RSA.import_key(key_der)
    cipher = PKCS1_v1_5.new(key)
    encrypted_bytes = cipher.encrypt(password.encode("utf-8"))
    return base64.b64encode(encrypted_bytes).decode("utf-8")


def login_shadowbot(
    username: str,
    password: str,
    auth_header: str = DEFAULT_BASIC_AUTH,
    timeout: int = 15
) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
    """
    登录影刀账号获取 access_token

    :param username: 接收方/目标账号 (手机号或用户名)
    :param password: 密码 (明文)
    :return: (is_success, message, token_data)
    """
    if not username or not password:
        return False, "用户名或密码不能为空", None

    try:
        encrypted_pwd = encrypt_password(password)
    except Exception as e:
        return False, f"密码 RSA 加密失败: {e}", None

    url = "https://api.yingdao.com/oauth/token"
    headers = {
        "Authorization": auth_header,
        "Content-Type": "application/x-www-form-urlencoded",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
    }
    data = {
        "crypt": "metal",
        "grant_type": "password",
        "scope": "all",
        "username": username.strip(),
        "password": encrypted_pwd
    }

    try:
        resp = requests.post(url, headers=headers, data=data, timeout=timeout)
        if resp.status_code == 200:
            res_json = resp.json()
            if "access_token" in res_json:
                return True, "登录成功", res_json
            else:
                msg = res_json.get("msg") or res_json.get("error_description") or "未知错误"
                return False, f"登录失败: {msg}", res_json
        else:
            try:
                err_json = resp.json()
                msg = err_json.get("msg") or err_json.get("error_description") or resp.text
            except Exception:
                msg = resp.text
            return False, f"登录失败 (HTTP {resp.status_code}): {msg}", None
    except requests.exceptions.RequestException as e:
        return False, f"网络请求异常: {e}", None
