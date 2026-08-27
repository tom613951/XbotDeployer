"""
Xbot Deployer 命令行交互工具 (CLI)
"""
import sys
import argparse
from datetime import datetime
from core.scanner import scan_local_apps
from core.auth import login_shadowbot
from core.deployer import ShadowBotDeployer
from core.packager import build_app_package


def run_cli():
    parser = argparse.ArgumentParser(description="影刀应用一键迁移与打包工具 (CLI版)")
    subparsers = parser.add_subparsers(dest="command", help="子命令")

    # 1. list
    subparsers.add_parser("list", help="列出本地所有影刀应用")

    # 2. export
    export_parser = subparsers.add_parser("export", help="导出指定应用为 Zip 压缩包")
    export_parser.add_argument("--uuid", required=True, help="应用的 UUID")
    export_parser.add_argument("--out", default="./output", help="导出保存目录")
    export_parser.add_argument("--encrypt", action="store_true", help="是否编译加密 Python 源码")

    # 3. deploy
    deploy_parser = subparsers.add_parser("deploy", help="跨账号一键迁移应用")
    deploy_parser.add_argument("--uuid", required=True, help="要迁移的应用 UUID")
    deploy_parser.add_argument("--user", required=True, help="接收方影刀账号")
    deploy_parser.add_argument("--pwd", required=True, help="接收方影刀密码")
    deploy_parser.add_argument("--name", default=None, help="自定义新应用名称 (默认自动追加接收时间)")
    deploy_parser.add_argument("--encrypt", action="store_true", help="是否编译加密 Python 源码")

    args = parser.parse_args()

    if not args.command:
        parser.print_help()
        return

    if args.command == "list":
        apps = scan_local_apps()
        print(f"=== 本地影刀应用列表 (共 {len(apps)} 个) ===")
        print(f"{'序号':<4} | {'UUID':<36} | {'修改时间':<19} | {'大小':<8} | {'应用名称'}")
        print("-" * 90)
        for i, app in enumerate(apps, 1):
            print(f"{i:<4} | {app['uuid']:<36} | {app['mtime_str']:<19} | {app['size_str']:<8} | {app['name']}")
        return

    if args.command == "export":
        apps = scan_local_apps()
        target = next((a for a in apps if a["uuid"] == args.uuid), None)
        if not target:
            print(f"❌ 找不到 UUID 为 [{args.uuid}] 的应用")
            sys.exit(1)

        print(f"📦 正在打包应用 [{target['name']}]...")
        try:
            zip_path, md5_val, _ = build_app_package(
                robot_dir=target["robot_dir"],
                encrypt_python=args.encrypt,
                output_dir=args.out
            )
            print(f"✅ 导出成功！\n文件路径: {zip_path}\nMD5: {md5_val}")
        except Exception as e:
            print(f"❌ 导出失败: {e}")
            sys.exit(1)
        return

    if args.command == "deploy":
        apps = scan_local_apps()
        target = next((a for a in apps if a["uuid"] == args.uuid), None)
        if not target:
            print(f"❌ 找不到 UUID 为 [{args.uuid}] 的应用")
            sys.exit(1)

        print(f"🔑 正在登录接收方账号 [{args.user}]...")
        ok, msg, token_res = login_shadowbot(args.user, args.pwd)
        if not ok or not token_res:
            print(f"❌ 登录失败: {msg}")
            sys.exit(1)

        token = token_res["access_token"]
        print("✅ 登录成功！")

        if args.name:
            target_name = args.name
        else:
            now_str = datetime.now().strftime("%Y年%m月%d日 %H时%M分%S秒")
            target_name = f"{target['name']}_云迁_接收于{now_str}"

        deployer = ShadowBotDeployer()
        ok, res_msg = deployer.deploy_single_app(
            target_token=token,
            robot_dir=target["robot_dir"],
            new_app_name=target_name,
            encrypt_python=args.encrypt,
            log_callback=print
        )
        if not ok:
            sys.exit(1)


if __name__ == "__main__":
    run_cli()
