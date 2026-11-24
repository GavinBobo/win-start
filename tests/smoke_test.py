"""端到端冒烟测试：真实启动管理服务，验证启动/日志/重启/停止/崩溃拉起/配置。"""

import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TMP = ROOT / "tests" / "tmp"
PORT = 8899
BASE = f"http://127.0.0.1:{PORT}"

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

passed = 0


def check(condition, message):
    global passed
    if not condition:
        raise AssertionError(message)
    passed += 1
    print(f"  ok  {message}")


def request(method, path, body=None, timeout=20, token=None, base=None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers = {}
    if data:
        headers["Content-Type"] = "application/json"
    if token:
        headers["X-Auth-Token"] = token
    req = urllib.request.Request(
        (base or BASE) + path,
        data=data,
        method=method,
        headers=headers,
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def get(path, timeout=20):
    return request("GET", path, timeout=timeout)


def post(path, body=None, timeout=30):
    return request("POST", path, body, timeout=timeout)


def wait_for(fn, timeout=15, interval=0.25, label="条件"):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = fn()
            if last:
                return last
        except (urllib.error.URLError, ConnectionError):
            pass
        time.sleep(interval)
    raise AssertionError(f"等待超时：{label}（最后结果 {last!r}）")


def app_state(app_id):
    return next(a for a in get("/api/state")["apps"] if a["id"] == app_id)


def app_logs(app_id, after=0):
    return get(f"/api/apps/{app_id}/logs?after={after}&limit=2000")["lines"]


def main():
    if TMP.exists():
        shutil.rmtree(TMP)
    TMP.mkdir(parents=True, exist_ok=True)
    for check_port in (PORT, PORT + 1):
        if health_ok(check_port):
            try:
                request("POST", "/api/manager/shutdown",
                        base=f"http://127.0.0.1:{check_port}")
                time.sleep(1.0)
            except Exception:  # noqa: BLE001
                pass
    if health_ok() or health_ok(PORT + 1):
        release_port()
        time.sleep(0.5)
    config_path = TMP / "config.json"
    config_path.write_text(json.dumps({"port": PORT, "apps": []}), encoding="utf-8")

    python = sys.executable
    env = dict(os.environ)
    env.update({"PYTHONUTF8": "1", "PYTHONIOENCODING": "utf-8"})
    proc = subprocess.Popen(
        [python, str(ROOT / "manager.py"), "--config", str(config_path)],
        cwd=str(ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        env=env,
    )
    try:
        print("[1] 启动管理服务")
        wait_for(lambda: get("/api/health"), 25, 0.3, "管理服务健康检查")
        check(True, "服务已监听 /api/health")

        print("[2] 开机自启状态查询")
        auto = get("/api/autostart")
        check(isinstance(auto, dict) and "installed" in auto, "可查询计划任务状态")

        print("[3] 创建并启动 Python 应用")
        created = post("/api/apps", {
            "app": {
                "name": "ticker",
                "command": f'"{python}" -u tests/fixtures/ticker.py',
                "cwd": str(ROOT),
                "shell": "powershell",
                "env": {"PORT": "8123"},
                "auto_start": False,
                "restart_on_crash": True,
            }
        })
        app_id = created["app"]["id"]
        check(app_id == "ticker", "应用创建成功，ID 为 ticker")

        started = post(f"/api/apps/{app_id}/start")
        check(started["ok"] is True, "启动接口返回成功")
        first_pid = started["app"]["pid"]
        check(isinstance(first_pid, int) and first_pid > 0, f"获得进程 PID {first_pid}")

        wait_for(lambda: app_state(app_id)["status"] == "running", 10, 0.2, "应用进入 running")
        check(True, "应用状态为运行中")

        print("[4] 日志与中文编码")
        wait_for(lambda: any("tick 1" in l["text"] for l in app_logs(app_id)), 10, 0.3, "出现 tick 日志")
        lines = app_logs(app_id)
        text = "\n".join(l["text"] for l in lines)
        check("ticker 启动，中文测试" in text, "中文输出无乱码")
        check("PORT=8123" in text, "环境变量已传入")

        print("[5] 重启")
        restarted = post(f"/api/apps/{app_id}/restart")
        check(restarted["ok"] is True, "重启接口返回成功")
        second_pid = restarted["app"]["pid"]
        check(isinstance(second_pid, int) and second_pid != first_pid, "重启后 PID 发生变化")
        wait_for(lambda: any("ticker 启动" in l["text"] for l in app_logs(app_id)), 8, 0.3,
                 "重启后新日志")
        check(True, "重启后继续输出日志")

        print("[6] 停止")
        stopped = post(f"/api/apps/{app_id}/stop")
        check(stopped["ok"] is True, "停止接口返回成功")
        check(app_state(app_id)["status"] == "stopped", "应用状态为已停止")
        check(app_state(app_id)["pid"] is None, "PID 已清空")

        log_file = ROOT / "logs" / f"{app_id}.log"
        check(log_file.exists() and "tick" in log_file.read_text(encoding="utf-8"),
              "日志已落盘到 logs 目录")

        print("[7] 崩溃自动重启")
        crash = post("/api/apps", {
            "app": {
                "name": "crasher",
                "command": f'"{python}" -u tests/fixtures/crasher.py',
                "cwd": str(ROOT),
                "restart_on_crash": True,
                "max_restarts": 1,
                "auto_start": False,
            }
        })["app"]
        post(f"/api/apps/{crash['id']}/start")
        wait_for(lambda: app_state(crash["id"])["restarts"] >= 1, 12, 0.3, "自动重启计数增加")
        check(True, "崩溃后触发了自动重启")
        def exhausted():
            current = app_state(crash["id"])
            if current["status"] == "crashed" and not current["restart_pending"] and current["restarts"] >= 1:
                return current
            return None

        final = wait_for(exhausted, 20, 0.3, "达到重启上限后停在 crashed")
        check("自动重启上限" in final["last_error"], "重启上限提示清晰")

        print("[8] CMD 启动方式")
        cmd_app = post("/api/apps", {
            "app": {
                "name": "cmd-echo",
                "command": "echo hello-cmd",
                "shell": "cmd",
                "auto_start": False,
                "restart_on_crash": False,
            }
        })["app"]
        post(f"/api/apps/{cmd_app['id']}/start")
        wait_for(lambda: any("hello-cmd" in l["text"] for l in app_logs(cmd_app["id"])),
                 10, 0.3, "CMD 输出")
        check(True, "CMD 模式可执行命令并采集输出")

        print("[9] 配置读写")
        cfg = get("/api/config")
        check(len(cfg["apps"]) == 3, "配置中包含 3 个应用")
        cfg["startup_delay"] = 1
        saved = request("PUT", "/api/config", cfg)
        check(saved["ok"] is True and saved["needs_restart"] is False, "保存配置成功")
        check(get("/api/config")["startup_delay"] == 1, "配置已持久化")

        print("[10] 清空日志")
        post(f"/api/apps/{app_id}/clear-logs")
        check(app_logs(app_id) == [], "日志已清空")
        check(log_file.exists() and log_file.stat().st_size == 0, "日志文件已清空")

        print("[11] 删除应用")
        for app in list(get("/api/state")["apps"]):
            request("DELETE", f"/api/apps/{app['id']}")
        check(get("/api/state")["apps"] == [], "全部应用已删除")

        print("[12] 访问令牌保护")
        cfg = get("/api/config")
        cfg["auth_token"] = "smoke-token-123"
        request("PUT", "/api/config", cfg)
        try:
            get("/api/state")
            raise AssertionError("未携带令牌的请求不应成功")
        except urllib.error.HTTPError as exc:
            check(exc.code == 401, "未携带令牌返回 401")
        req = urllib.request.Request(BASE + "/api/state", headers={"X-Auth-Token": "smoke-token-123"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            check(resp.status == 200 and json.loads(resp.read())["apps"] == [],
                  "携带正确令牌可正常访问")
        cfg["auth_token"] = ""
        request("PUT", "/api/config", cfg, token="smoke-token-123")
        check(get("/api/config")["auth_token"] == "", "令牌已关闭")

        print("[13] 重启管理服务")
        post("/api/manager/restart")
        wait_for(lambda: get("/api/health"), 30, 0.5, "管理服务重启后恢复")
        check(True, "管理服务已重新监听")
        check(get("/api/state")["apps"] == [], "重启后配置仍然有效")

        print("[14] 修改端口并重启")
        cfg = get("/api/config")
        cfg["port"] = PORT + 1
        request("PUT", "/api/config", cfg)
        post("/api/manager/restart")
        new_base = f"http://127.0.0.1:{PORT + 1}"
        wait_for(lambda: request("GET", "/api/health", timeout=2, base=new_base),
                 30, 0.5, "新端口恢复")
        check(True, f"服务已切换到新端口 {PORT + 1}")
        wait_for(lambda: not health_ok(), 10, 0.3, "旧端口已释放")
        check(True, "旧端口已释放")

        print("[15] 关闭管理服务")
        request("POST", "/api/manager/shutdown", base=new_base)
        wait_for(lambda: proc.poll() is not None, 15, 0.3, "管理进程退出")
        try:
            wait_for(lambda: not health_ok() and not health_ok(PORT + 1), 15, 0.3, "端口已释放")
        except AssertionError:
            info = ""
            try:
                info = json.dumps(get("/api/state")["server"], ensure_ascii=False)
            except Exception as exc:  # noqa: BLE001
                info = f"state error: {exc}"
            print("仍然响应健康检查：", info)
            raise
        check(True, "管理服务已正常退出")

        print(f"\n全部通过：{passed} 项检查")
        return 0
    finally:
        if proc.poll() is None:
            proc.kill()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pass
        for check_port in (PORT, PORT + 1):
            if health_ok(check_port):
                try:
                    request("POST", "/api/manager/shutdown",
                            base=f"http://127.0.0.1:{check_port}")
                except Exception:  # noqa: BLE001
                    release_port()
        time.sleep(0.5)
        if health_ok() or health_ok(PORT + 1):
            release_port()
        out = proc.stdout.read().decode("utf-8", "replace") if proc.stdout else ""
        if out.strip():
            print("\n--- 管理服务输出 ---")
            print(out.strip()[-4000:])


def health_ok(port=None):
    try:
        request("GET", "/api/health", timeout=2,
                base=f"http://127.0.0.1:{port}" if port else None)
        return True
    except Exception:  # noqa: BLE001
        return False


def release_port():
    script = (
        f"Get-NetTCPConnection -LocalPort {PORT},{PORT + 1} -State Listen -ErrorAction SilentlyContinue "
        "| ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(["powershell", "-NoProfile", "-Command", script],
                   capture_output=True, timeout=30)


if __name__ == "__main__":
    sys.exit(main())
