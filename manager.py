#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
应用管家 (App Manager)

一个零依赖的 Windows 常驻小程序，用来托管 Python / Node 等命令行应用：
  * 网页控制台：查看运行状态与实时日志，启动 / 停止 / 重启
  * 应用崩溃可自动拉起
  * 可注册为 Windows 计划任务，开机（无需登录）自动运行

只使用 Python 标准库，不需要 pip 安装任何东西。
"""

from __future__ import annotations

import argparse
import hmac
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import traceback
import urllib.request
import webbrowser
from collections import deque
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

APP_VERSION = "1.0.0"
TASK_NAME = "AppManager"

ROOT = Path(__file__).resolve().parent
WEB_DIR = ROOT / "web"
LOGS_DIR = ROOT / "logs"
RUN_DIR = ROOT / "run"

CREATE_NO_WINDOW = 0x08000000
CREATE_NEW_PROCESS_GROUP = 0x00000200

POWERSHELL = shutil.which("powershell.exe") or r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"


# --------------------------------------------------------------------------- #
# 小工具
# --------------------------------------------------------------------------- #

def now_ms() -> int:
    return int(time.time() * 1000)


def safe_filename(text: str, fallback: str = "app") -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "-", str(text or "")).strip("-._")
    return cleaned[:64] or fallback


def decode_output(raw: bytes) -> str:
    return (raw or b"").decode("utf-8", "replace").strip()


def decode_line(raw: bytes) -> str:
    """子进程输出优先按 UTF-8 解码，失败时兼容 GBK（中文 Windows 常见）。"""
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        try:
            return raw.decode("gbk")
        except UnicodeDecodeError:
            return raw.decode("utf-8", "replace")


def setup_console_encoding():
    # pythonw.exe 下 stdout/stderr 为 None，补一个空输出，避免 print 报错
    if sys.stdout is None:
        sys.stdout = open(os.devnull, "w", encoding="utf-8")
    if sys.stderr is None:
        sys.stderr = open(os.devnull, "w", encoding="utf-8")
    if os.name != "nt":
        return
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        kernel32.SetConsoleOutputCP(65001)
        kernel32.SetConsoleCP(65001)
    except Exception:  # noqa: BLE001
        pass
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except Exception:  # noqa: BLE001
            pass


def atomic_write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    os.replace(tmp, path)


def instance_running(host: str, port: int) -> bool:
    check_host = "127.0.0.1" if host in ("0.0.0.0", "127.0.0.1", "localhost") else host
    try:
        with urllib.request.urlopen(f"http://{check_host}:{port}/api/health", timeout=2) as resp:
            return resp.status == 200
    except Exception:  # noqa: BLE001
        return False


class ApiError(Exception):
    def __init__(self, message: str, code: int = 400):
        super().__init__(message)
        self.code = code


# --------------------------------------------------------------------------- #
# 日志缓冲（内存 + 文件，自动轮转）
# --------------------------------------------------------------------------- #

class LogBuffer:
    def __init__(self, max_lines: int = 2000, path: Path | None = None,
                 max_bytes: int = 5 * 1024 * 1024, backups: int = 3):
        self.lock = threading.Lock()
        self.max_lines = max(100, int(max_lines))
        self.lines: deque = deque(maxlen=self.max_lines)
        self.seq = 0
        self.path = path
        self.max_bytes = max_bytes
        self.backups = backups
        self._fh = None
        self._writes = 0

    # -- 内部 ---------------------------------------------------------------
    def _open(self):
        if self.path is None:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if self._fh is None:
            self._fh = open(self.path, "ab", buffering=0)

    def _close(self):
        if self._fh is not None:
            try:
                self._fh.close()
            except OSError:
                pass
            self._fh = None

    def _maybe_rotate(self):
        self._writes += 1
        if self.path is None or self._fh is None or self._writes % 200 != 0:
            return
        try:
            if self._fh.tell() < self.max_bytes:
                return
        except OSError:
            return
        try:
            self._close()
            for i in range(self.backups - 1, 0, -1):
                src = self.path.with_name(self.path.name + f".{i}")
                dst = self.path.with_name(self.path.name + f".{i + 1}")
                if src.exists():
                    os.replace(src, dst)
            if self.path.exists():
                os.replace(self.path, self.path.with_name(self.path.name + ".1"))
            self._open()
        except OSError:
            self._open()

    # -- 对外 ---------------------------------------------------------------
    def append(self, text: str, ts: float | None = None):
        ts = time.time() if ts is None else ts
        entry = {
            "seq": 0,
            "ts": int(ts * 1000),
            "text": str(text).replace("\x00", "")[:8000],
        }
        with self.lock:
            self.seq += 1
            entry["seq"] = self.seq
            self.lines.append(entry)
            self._open()
            if self._fh is not None:
                stamp = datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M:%S")
                try:
                    self._fh.write(f"[{stamp}] {entry['text']}\n".encode("utf-8", "replace"))
                except OSError:
                    pass
            self._maybe_rotate()
        return entry

    def read(self, after: int = 0, limit: int = 800):
        limit = max(1, min(int(limit), 5000))
        with self.lock:
            if after <= 0:
                items = list(self.lines)[-limit:]
            else:
                items = [x for x in self.lines if x["seq"] > after][:limit]
            return items, self.seq

    def clear(self):
        with self.lock:
            self._close()
            self.lines.clear()
            if self.path is not None:
                try:
                    self.path.parent.mkdir(parents=True, exist_ok=True)
                    with open(self.path, "wb"):
                        pass
                except OSError:
                    pass
            return self.seq

    def dump_text(self) -> str:
        with self.lock:
            items = list(self.lines)
        if not items:
            return ""
        return "\n".join(
            f"[{datetime.fromtimestamp(x['ts'] / 1000).strftime('%Y-%m-%d %H:%M:%S')}] {x['text']}"
            for x in items
        ) + "\n"


# --------------------------------------------------------------------------- #
# 配置
# --------------------------------------------------------------------------- #

DEFAULT_CONFIG = {
    "port": 8787,
    "host": "127.0.0.1",
    "auth_token": "",
    "startup_delay": 3,
    "extra_path": "",
    "env": {},
    "apps": [],
}


def clamp_int(value, low: int, high: int, default: int) -> int:
    try:
        value = int(value)
    except (TypeError, ValueError):
        return default
    return max(low, min(high, value))


def normalize_app(raw, existing_id: str | None = None) -> dict:
    if not isinstance(raw, dict):
        raise ApiError("应用配置格式不正确")

    name = str(raw.get("name") or "").strip()
    if not name:
        raise ApiError("请填写应用名称")
    if len(name) > 60:
        raise ApiError("应用名称不能超过 60 个字符")

    command = str(raw.get("command") or "").strip()
    if not command:
        raise ApiError("请填写启动命令")
    if len(command) > 8000:
        raise ApiError("启动命令过长")

    cwd = str(raw.get("cwd") or "").strip()
    if cwd:
        cwd = os.path.expandvars(cwd)
        if not os.path.isdir(cwd):
            raise ApiError(f"工作目录不存在：{cwd}")
        cwd = os.path.abspath(cwd)
    else:
        cwd = str(ROOT)

    shell = str(raw.get("shell") or "powershell").lower()
    if shell not in ("powershell", "cmd"):
        raise ApiError("启动方式只能是 powershell 或 cmd")

    env_raw = raw.get("env") or {}
    if not isinstance(env_raw, dict):
        raise ApiError("环境变量格式不正确")
    env = {}
    for key, value in env_raw.items():
        key = str(key).strip()
        if not key:
            continue
        env[key] = str(value)

    return {
        "id": existing_id or "",
        "name": name,
        "command": command,
        "cwd": cwd,
        "shell": shell,
        "env": env,
        "auto_start": bool(raw.get("auto_start", True)),
        "restart_on_crash": bool(raw.get("restart_on_crash", True)),
        "start_delay": clamp_int(raw.get("start_delay", 0), 0, 600, 0),
        "max_restarts": clamp_int(raw.get("max_restarts", 10), 0, 100, 10),
        "max_log_lines": clamp_int(raw.get("max_log_lines", 2000), 200, 20000, 2000),
    }


def normalize_config(raw, existing_ids: set[str] | None = None) -> dict:
    if not isinstance(raw, dict):
        raise ApiError("配置格式不正确，应为 JSON 对象")

    cfg = {
        "port": clamp_int(raw.get("port", DEFAULT_CONFIG["port"]), 1, 65535, 8787),
        "host": str(raw.get("host") or "127.0.0.1").strip(),
        "auth_token": str(raw.get("auth_token") or "").strip(),
        "startup_delay": clamp_int(raw.get("startup_delay", 3), 0, 3600, 3),
        "extra_path": str(raw.get("extra_path") or "").strip(),
        "env": {},
        "apps": [],
    }
    if cfg["host"] not in ("127.0.0.1", "0.0.0.0", "localhost"):
        raise ApiError("监听地址只能是 127.0.0.1 或 0.0.0.0")

    env_raw = raw.get("env") or {}
    if not isinstance(env_raw, dict):
        raise ApiError("全局环境变量格式不正确")
    cfg["env"] = {str(k).strip(): str(v) for k, v in env_raw.items() if str(k).strip()}

    apps_raw = raw.get("apps") or []
    if not isinstance(apps_raw, list):
        raise ApiError("apps 必须是数组")

    seen = set()
    used = set(existing_ids or set())
    for item in apps_raw:
        if not isinstance(item, dict):
            raise ApiError("apps 中每一项都必须是对象")
        app_id = str(item.get("id") or "").strip()
        if app_id:
            if not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", app_id):
                raise ApiError(f"应用 ID 不合法：{app_id}")
            if app_id in seen:
                raise ApiError(f"应用 ID 重复：{app_id}")
        app = normalize_app(item, app_id or None)
        if not app_id:
            slug = safe_filename(app["name"], "")
            candidate = slug or "app"
            n = 2
            while candidate in used:
                candidate = f"{slug or 'app'}-{n}"
                n += 1
            app["id"] = candidate
        seen.add(app["id"])
        used.add(app["id"])
        cfg["apps"].append(app)
    return cfg


# --------------------------------------------------------------------------- #
# 单个应用
# --------------------------------------------------------------------------- #

class ManagedApp:
    def __init__(self, manager: "Manager", cfg: dict):
        self.manager = manager
        self.cfg = cfg
        self.lock = threading.RLock()
        self.proc: subprocess.Popen | None = None
        self.status = "stopped"          # stopped | starting | running | stopping | crashed
        self.pid: int | None = None
        self.exit_code: int | None = None
        self.started_at: float | None = None
        self.stopped_at: float | None = None
        self.stop_requested = False
        self.generation = 0
        self.restarts = 0
        self.restart_pending = False
        self.last_error = ""
        self.log = LogBuffer(
            cfg.get("max_log_lines", 2000),
            LOGS_DIR / f"{safe_filename(cfg['id'])}.log",
        )

    # -- 属性 ---------------------------------------------------------------
    @property
    def id(self) -> str:
        return self.cfg["id"]

    @property
    def name(self) -> str:
        return self.cfg["name"]

    def is_alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def snapshot(self) -> dict:
        with self.lock:
            running = self.is_alive()
            return {
                "id": self.id,
                "name": self.name,
                "status": self.status,
                "running": running,
                "pid": self.pid,
                "exit_code": self.exit_code,
                "started_at": int(self.started_at * 1000) if self.started_at else None,
                "stopped_at": int(self.stopped_at * 1000) if self.stopped_at else None,
                "uptime_seconds": int(time.time() - self.started_at) if (running and self.started_at) else 0,
                "restarts": self.restarts,
                "restart_pending": self.restart_pending,
                "last_error": self.last_error,
                "log_seq": self.log.seq,
                "command": self.cfg["command"],
                "cwd": self.cfg["cwd"],
                "shell": self.cfg["shell"],
                "auto_start": self.cfg["auto_start"],
                "restart_on_crash": self.cfg["restart_on_crash"],
                "start_delay": self.cfg["start_delay"],
                "max_restarts": self.cfg["max_restarts"],
                "env": self.cfg.get("env", {}),
                "log_file": str(self.log.path) if self.log.path else None,
            }

    # -- 启动 ---------------------------------------------------------------
    def _write_wrapper(self) -> list[str]:
        RUN_DIR.mkdir(parents=True, exist_ok=True)
        base = safe_filename(self.id)
        cwd = self.cfg["cwd"]
        command = self.cfg["command"]
        if self.cfg["shell"] == "cmd":
            script = RUN_DIR / f"{base}.cmd"
            content = "\r\n".join([
                "@echo off",
                "chcp 65001 >nul",
                f'cd /d "{cwd}"',
                command,
                "exit /b %ERRORLEVEL%",
            ]) + "\r\n"
            script.write_text(content, encoding="utf-8", newline="")
            return ["cmd.exe", "/d", "/c", str(script)]

        script = RUN_DIR / f"{base}.ps1"
        quoted_cwd = cwd.replace("'", "''")
        # PowerShell 里以引号开头的路径需要 & 调用运算符
        ps_command = command.lstrip()
        if ps_command.startswith('"') or ps_command.startswith("'"):
            ps_command = "& " + ps_command
        content = "\r\n".join([
            "$ErrorActionPreference = 'Continue'",
            "$ProgressPreference = 'SilentlyContinue'",
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = [Console]::OutputEncoding",
            f"Set-Location -LiteralPath '{quoted_cwd}'",
            ps_command,
            "exit $LASTEXITCODE",
        ]) + "\r\n"
        script.write_text(content, encoding="utf-8-sig", newline="")
        # 通过 -Command 先设置输出编码，再调用脚本，保证脚本自身的报错信息也是 UTF-8
        outer = (
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
            "$OutputEncoding = [Console]::OutputEncoding; "
            f"& '{str(script).replace(chr(39), chr(39) * 2)}'; exit $LASTEXITCODE"
        )
        return [POWERSHELL, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", outer]

    def _child_env(self) -> dict:
        env = os.environ.copy()
        env.update({
            "PYTHONUNBUFFERED": "1",
            "PYTHONUTF8": "1",
            "PYTHONIOENCODING": "utf-8",
        })
        gcfg = self.manager.config
        extra_path = (gcfg.get("extra_path") or "").strip()
        if extra_path:
            env["PATH"] = extra_path.rstrip(";") + ";" + env.get("PATH", "")
        for key, value in (gcfg.get("env") or {}).items():
            env[str(key)] = str(value)
        for key, value in (self.cfg.get("env") or {}).items():
            env[str(key)] = str(value)
        env.setdefault("APP_MANAGER_APP_ID", self.id)
        return env

    def start(self) -> tuple[bool, str]:
        with self.lock:
            if self.is_alive():
                return False, "应用已在运行"
            if self.status in ("starting", "stopping"):
                return False, "应用正在切换状态，请稍候"

            self.status = "starting"
            self.exit_code = None
            self.stop_requested = False
            self.last_error = ""
            self.generation += 1
            generation = self.generation

        try:
            cmdline = self._write_wrapper()
            flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP
            proc = subprocess.Popen(
                cmdline,
                cwd=self.cfg["cwd"],
                env=self._child_env(),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                bufsize=0,
                creationflags=flags,
            )
        except FileNotFoundError as exc:
            with self.lock:
                self.status = "stopped"
                self.last_error = f"启动失败：{exc}"
            self.log.append(f"[管家] 启动失败：{exc}")
            return False, f"启动失败：{exc}"
        except OSError as exc:
            with self.lock:
                self.status = "stopped"
                self.last_error = f"启动失败：{exc}"
            self.log.append(f"[管家] 启动失败：{exc}")
            return False, f"启动失败：{exc}"

        with self.lock:
            self.proc = proc
            self.pid = proc.pid
            self.status = "running"
            self.started_at = time.time()
            self.stopped_at = None
        self.log.append(f"[管家] 已启动，PID {proc.pid}（{self.cfg['shell']}）")

        threading.Thread(target=self._pump_output, args=(proc,), daemon=True,
                         name=f"log-{self.id}").start()
        threading.Thread(target=self._wait_exit, args=(proc, generation), daemon=True,
                         name=f"wait-{self.id}").start()
        self.manager.system_log.append(f"[{self.name}] 已启动，PID {proc.pid}")
        return True, "已启动"

    def manual_start(self) -> tuple[bool, str]:
        """用户手动启动：重置自动重启计数。"""
        with self.lock:
            self.restarts = 0
        return self.start()

    def _pump_output(self, proc: subprocess.Popen):
        fd = proc.stdout.fileno()
        buf = b""
        while True:
            try:
                chunk = os.read(fd, 8192)
            except OSError:
                break
            if not chunk:
                break
            buf += chunk
            while True:
                positions = [p for p in (buf.find(b"\n"), buf.find(b"\r")) if p >= 0]
                if not positions:
                    break
                idx = min(positions)
                line = buf[:idx]
                buf = buf[idx + 1:]
                if line:
                    self.log.append(decode_line(line))
            if len(buf) > 16384:
                self.log.append(decode_line(buf))
                buf = b""
        if buf:
            self.log.append(decode_line(buf))
        try:
            proc.stdout.close()
        except OSError:
            pass

    def _wait_exit(self, proc: subprocess.Popen, generation: int):
        try:
            code = proc.wait()
        except Exception:  # noqa: BLE001
            code = -1

        time.sleep(0.15)  # 让日志线程把最后几行读完
        with self.lock:
            if self.proc is not proc:
                return
            runtime = time.time() - self.started_at if self.started_at else 0
            if runtime > 120:
                # 稳定运行过一段时间，之前的崩溃计数不再累计
                self.restarts = 0
            self.proc = None
            self.pid = None
            self.exit_code = code
            self.stopped_at = time.time()
            stopped_by_user = self.stop_requested
            if stopped_by_user:
                self.status = "stopped"
            elif code != 0:
                self.status = "crashed"
                self.last_error = f"进程异常退出（代码 {code}）"
            else:
                self.status = "stopped"
                self.last_error = "进程已退出（代码 0）"
            should_restart = (
                not stopped_by_user
                and self.cfg["restart_on_crash"]
                and generation == self.generation
                and self.restarts < self.cfg["max_restarts"]
            )
            if (not stopped_by_user) and code != 0 and self.cfg["restart_on_crash"] \
                    and self.restarts >= self.cfg["max_restarts"]:
                self.last_error = (
                    f"进程异常退出（代码 {code}），已达到自动重启上限"
                    f"（{self.cfg['max_restarts']} 次）"
                )
            if should_restart:
                self.restarts += 1
                self.restart_pending = True
                attempt = self.restarts

        if not stopped_by_user:
            self.log.append(f"[管家] 进程退出，代码 {code}")
            self.manager.system_log.append(f"[{self.name}] 进程退出，代码 {code}")

        if should_restart:
            delay = min(30, 2 ** min(attempt, 5))
            self.log.append(f"[管家] {delay} 秒后自动重启（第 {attempt} 次）")
            timer = threading.Timer(delay, self._auto_restart, args=(generation,))
            timer.daemon = True
            timer.start()
        else:
            with self.lock:
                if generation == self.generation:
                    self.restart_pending = False

    def _auto_restart(self, generation: int):
        with self.lock:
            if generation != self.generation or self.stop_requested:
                self.restart_pending = False
                return
            self.restart_pending = False
        if self.is_alive():
            return
        ok, msg = self.start()
        if not ok:
            self.log.append(f"[管家] 自动重启失败：{msg}")

    # -- 停止 ---------------------------------------------------------------
    def stop(self, timeout: float = 8.0) -> tuple[bool, str]:
        with self.lock:
            proc = self.proc
            pid = self.pid
            if proc is None or not self.is_alive():
                self.status = "stopped"
                self.proc = None
                self.pid = None
                self.restart_pending = False
                return True, "应用已停止"
            self.stop_requested = True
            self.generation += 1
            self.status = "stopping"
            self.restart_pending = False

        self._kill_tree(pid)
        deadline = time.time() + timeout
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            time.sleep(0.1)
        if proc.poll() is None:
            self._kill_tree(pid)
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                pass

        with self.lock:
            if self.proc is proc:
                self.proc = None
                self.pid = None
                self.status = "stopped"
                self.stopped_at = time.time()
        self.log.append("[管家] 已停止")
        self.manager.system_log.append(f"[{self.name}] 已停止")
        return True, "已停止"

    @staticmethod
    def _kill_tree(pid: int | None):
        if not pid:
            return
        try:
            subprocess.run(
                ["taskkill", "/PID", str(pid), "/T", "/F"],
                capture_output=True,
                timeout=15,
                creationflags=CREATE_NO_WINDOW,
            )
        except (OSError, subprocess.SubprocessError):
            pass

    def clear_logs(self):
        self.log.clear()
        return True

    def update_cfg(self, cfg: dict):
        with self.lock:
            self.cfg = cfg
        self.log.max_lines = cfg.get("max_log_lines", 2000)
        # deque 的 maxlen 不能改，重建一个
        if self.log.lines.maxlen != self.log.max_lines:
            self.log.lines = deque(self.log.lines, maxlen=self.log.max_lines)


# --------------------------------------------------------------------------- #
# 管理器
# --------------------------------------------------------------------------- #

class Manager:
    def __init__(self, config_path: Path, args):
        self.config_path = config_path
        self.args = args
        self.lock = threading.RLock()
        self.config: dict = dict(DEFAULT_CONFIG)
        self.apps: dict[str, ManagedApp] = {}
        self.system_log = LogBuffer(1500, LOGS_DIR / "_manager.log", max_bytes=2 * 1024 * 1024)
        self.started_at = time.time()
        self._autostart_cache = {"installed": False, "state": "", "checked_at": 0}
        self._autostart_lock = threading.Lock()
        self.httpd: ThreadingHTTPServer | None = None
        self._shutting_down = False
        self.stopping = False
        self._restarting = False
        self.stop_event = threading.Event()

    # -- 配置读写 -----------------------------------------------------------
    def load(self):
        LOGS_DIR.mkdir(parents=True, exist_ok=True)
        RUN_DIR.mkdir(parents=True, exist_ok=True)

        raw = None
        if self.config_path.exists():
            try:
                raw = json.loads(self.config_path.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError) as exc:
                raise SystemExit(f"配置文件读取失败：{self.config_path}\n{exc}")
        else:
            raw = dict(DEFAULT_CONFIG)
            raw["apps"] = []
            atomic_write_json(self.config_path, raw)
            self.system_log.append("[管家] 已创建默认配置文件")

        cfg = normalize_config(raw)
        if getattr(self.args, "port", None):
            cfg["port"] = int(self.args.port)
        if getattr(self.args, "host", None):
            cfg["host"] = str(self.args.host)

        self.config = cfg
        for app_cfg in cfg["apps"]:
            self.apps[app_cfg["id"]] = ManagedApp(self, app_cfg)
        self.system_log.append(
            f"[管家] 配置加载完成，共 {len(self.apps)} 个应用，监听 {cfg['host']}:{cfg['port']}"
        )

    def save_config(self, extra: dict | None = None):
        with self.lock:
            data = {
                "port": self.config["port"],
                "host": self.config["host"],
                "auth_token": self.config["auth_token"],
                "startup_delay": self.config["startup_delay"],
                "extra_path": self.config["extra_path"],
                "env": self.config["env"],
                "apps": [app.cfg for app in self.apps.values()],
            }
            if extra:
                data.update(extra)
            atomic_write_json(self.config_path, data)

    def apply_config(self, raw: dict) -> dict:
        """整体替换配置（用于“设置”页 / 直接编辑 JSON）。"""
        with self.lock:
            old = self.config
            new = normalize_config(raw, set(self.apps.keys()))
            needs_restart = (
                new["port"] != old["port"]
                or new["host"] != old["host"]
                or new["auth_token"] != old["auth_token"]
            )

            new_apps: dict[str, ManagedApp] = {}
            for app_cfg in new["apps"]:
                app_id = app_cfg["id"]
                if app_id in self.apps:
                    app = self.apps[app_id]
                    app.update_cfg(app_cfg)
                else:
                    app = ManagedApp(self, app_cfg)
                new_apps[app_id] = app

            for app_id, app in self.apps.items():
                if app_id not in new_apps:
                    app.stop()
                    self.system_log.append(f"[{app.name}] 已从配置中移除")

            self.apps = new_apps
            self.config = new
            self.save_config()
            self.system_log.append("[管家] 配置已保存")
            return {"ok": True, "needs_restart": needs_restart}

    # -- 应用操作 -----------------------------------------------------------
    def get_app(self, app_id: str) -> ManagedApp:
        app = self.apps.get(app_id)
        if app is None:
            raise ApiError("应用不存在", 404)
        return app

    def add_app(self, raw: dict) -> dict:
        with self.lock:
            app_cfg = normalize_app(raw)
            used = set(self.apps.keys())
            slug = safe_filename(app_cfg["name"], "")
            candidate = slug or "app"
            n = 2
            while candidate in used:
                candidate = f"{slug or 'app'}-{n}"
                n += 1
            app_cfg["id"] = candidate
            app = ManagedApp(self, app_cfg)
            self.apps[app_cfg["id"]] = app
            self.save_config()
            self.system_log.append(f"[{app.name}] 已添加")
            return app.snapshot()

    def update_app(self, app_id: str, raw: dict) -> dict:
        with self.lock:
            app = self.get_app(app_id)
            app_cfg = normalize_app(raw, existing_id=app_id)
            app.update_cfg(app_cfg)
            self.save_config()
            self.system_log.append(f"[{app.name}] 配置已更新")
            return app.snapshot()

    def delete_app(self, app_id: str) -> dict:
        with self.lock:
            app = self.get_app(app_id)
            app.stop()
            del self.apps[app_id]
            self.save_config()
            self.system_log.append(f"[{app.name}] 已删除")
            return {"ok": True}

    def reorder_apps(self, ids: list[str]):
        with self.lock:
            if sorted(ids) != sorted(self.apps.keys()):
                raise ApiError("应用列表不一致")
            self.apps = {app_id: self.apps[app_id] for app_id in ids}
            self.save_config()

    def start_all(self) -> list[dict]:
        results = []
        for app in list(self.apps.values()):
            ok, msg = app.manual_start()
            results.append({"id": app.id, "ok": ok, "message": msg})
        return results

    def stop_all(self):
        for app in list(self.apps.values()):
            try:
                app.stop()
            except Exception:  # noqa: BLE001
                traceback.print_exc()

    # -- 状态 ---------------------------------------------------------------
    def snapshot(self) -> dict:
        with self.lock:
            apps = [app.snapshot() for app in self.apps.values()]
            autostart = dict(self._autostart_cache)
        counts = {
            "total": len(apps),
            "running": sum(1 for a in apps if a["running"]),
            "crashed": sum(1 for a in apps if a["status"] == "crashed"),
        }
        return {
            "now": now_ms(),
            "server": {
                "version": APP_VERSION,
                "pid": os.getpid(),
                "host": self.config["host"],
                "port": self.config["port"],
                "started_at": int(self.started_at * 1000),
                "python": sys.executable,
                "root": str(ROOT),
                "config_path": str(self.config_path),
                "requires_token": bool(self.config["auth_token"]),
                "autostart": autostart,
            },
            "counts": counts,
            "apps": apps,
        }

    # -- 开机自启 -----------------------------------------------------------
    def autostart_status(self, force: bool = False) -> dict:
        with self._autostart_lock:
            age = time.time() - self._autostart_cache.get("checked_at", 0)
            if not force and age < 15:
                return dict(self._autostart_cache)

            result = {"installed": False, "state": "", "checked_at": time.time()}
            try:
                ps = (
                    "$ErrorActionPreference='SilentlyContinue';"
                    "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);"
                    f"$t=Get-ScheduledTask -TaskName '{TASK_NAME}';"
                    "if($t){$t|Select-Object TaskName,State|ConvertTo-Json -Compress}else{'null'}"
                )
                proc = subprocess.run(
                    [POWERSHELL, "-NoLogo", "-NoProfile", "-Command", ps],
                    capture_output=True,
                    timeout=20,
                    creationflags=CREATE_NO_WINDOW,
                )
                text = decode_output(proc.stdout)
                if text and text != "null":
                    data = json.loads(text)
                    if data:
                        result["installed"] = True
                        raw_state = str(data.get("State", ""))
                        result["state"] = {
                            "0": "未知", "1": "已禁用", "2": "排队中",
                            "3": "就绪", "4": "运行中",
                        }.get(raw_state, raw_state)
            except (OSError, subprocess.SubprocessError, json.JSONDecodeError):
                pass
            self._autostart_cache = result
            return dict(result)

    def set_autostart(self, action: str, mode: str = "boot") -> dict:
        if action not in ("install", "uninstall"):
            raise ApiError("未知操作")
        script = ROOT / "scripts" / ("install-autostart.ps1" if action == "install" else "uninstall-autostart.ps1")
        if not script.exists():
            raise ApiError(f"缺少脚本：{script}")

        args = [POWERSHELL, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                "-TaskName", TASK_NAME]
        if action == "install":
            if mode not in ("boot", "logon"):
                raise ApiError("启动方式只能是 boot 或 logon")
            args += [
                "-Mode", "Boot" if mode == "boot" else "Logon",
                "-Python", sys.executable,
                "-Script", str(Path(__file__).resolve()),
            ]
        try:
            proc = subprocess.run(args, capture_output=True, timeout=180,
                                  creationflags=CREATE_NO_WINDOW)
        except subprocess.TimeoutExpired:
            raise ApiError("注册计划任务超时，请用管理员 PowerShell 手动运行 scripts\\install-autostart.ps1")
        output = (decode_output(proc.stdout) + "\n" + decode_output(proc.stderr)).strip()
        ok = proc.returncode == 0
        self.system_log.append(
            f"[管家] {'安装' if action == 'install' else '移除'}开机自启：{'成功' if ok else '失败'} "
            f"(exit {proc.returncode})"
        )
        return {
            "ok": ok,
            "code": proc.returncode,
            "output": output,
            "autostart": self.autostart_status(force=True),
        }

    def refresh_autostart_async(self):
        """后台刷新计划任务状态，避免拖慢 /api/state。"""
        threading.Thread(target=self.autostart_status, kwargs={"force": True},
                         daemon=True, name="autostart-check").start()

    # -- 自身重启 -----------------------------------------------------------
    def _close_http(self):
        """立刻关闭监听套接字，保证后续请求不会再落到旧实例上。"""
        httpd = self.httpd
        self.httpd = None
        if httpd is None:
            return
        try:
            httpd.socket.close()
        except OSError:
            pass

        def finish():
            try:
                httpd.shutdown()
            except Exception:  # noqa: BLE001
                pass
            try:
                httpd.server_close()
            except Exception:  # noqa: BLE001
                pass

        threading.Thread(target=finish, daemon=True, name="http-close").start()

    def restart_self(self):
        if self._restarting:
            return
        self._restarting = True
        self.system_log.append("[管家] 正在重启管理服务……")
        self.stopping = True
        self._shutting_down = True
        self._close_http()
        self.stop_all()
        cmd = [sys.executable, str(Path(__file__).resolve())]
        if self.config_path != ROOT / "config.json":
            cmd += ["--config", str(self.config_path)]
        if getattr(self.args, "port", None):
            cmd += ["--port", str(self.args.port)]
        if getattr(self.args, "host", None):
            cmd += ["--host", str(self.args.host)]
        flags = CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP
        subprocess.Popen(cmd, cwd=str(ROOT), stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=flags, close_fds=True)
        threading.Timer(0.8, lambda: os._exit(0)).start()

    def shutdown(self):
        if self._shutting_down:
            return
        self._shutting_down = True
        self.stopping = True
        self.system_log.append("[管家] 收到退出信号，正在停止所有应用……")
        self._close_http()
        self.stop_all()
        self.stop_event.set()

    def boot_sequence(self):
        delay = int(self.config.get("startup_delay") or 0)
        if delay:
            self.system_log.append(f"[管家] 开机启动序列：等待 {delay} 秒")
            time.sleep(delay)
        for app in list(self.apps.values()):
            if not app.cfg.get("auto_start"):
                continue
            per_app_delay = int(app.cfg.get("start_delay") or 0)
            if per_app_delay:
                time.sleep(per_app_delay)
            if self._shutting_down:
                return
            ok, msg = app.manual_start()
            self.system_log.append(f"[管家] 自动启动 {app.name}：{msg}")


# --------------------------------------------------------------------------- #
# HTTP 服务
# --------------------------------------------------------------------------- #

STATIC_FILES = {
    "/": ("index.html", "text/html; charset=utf-8"),
    "/index.html": ("index.html", "text/html; charset=utf-8"),
    "/app.js": ("app.js", "application/javascript; charset=utf-8"),
    "/style.css": ("style.css", "text/css; charset=utf-8"),
    "/favicon.svg": ("favicon.svg", "image/svg+xml"),
}


class AppHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    # Windows 上 SO_REUSEADDR 会允许两个进程同时绑定同一端口，必须关掉
    allow_reuse_address = os.name != "nt"

    def __init__(self, addr, handler, manager: Manager):
        self.manager = manager
        super().__init__(addr, handler)

    def handle_error(self, request, client_address):
        exc = sys.exc_info()[1]
        if isinstance(exc, (ConnectionResetError, ConnectionAbortedError, BrokenPipeError)):
            return
        super().handle_error(request, client_address)


class RequestHandler(BaseHTTPRequestHandler):
    server_version = f"AppManager/{APP_VERSION}"
    protocol_version = "HTTP/1.1"

    @property
    def manager(self) -> Manager:
        return self.server.manager  # type: ignore[attr-defined]

    # -- 基础响应 -----------------------------------------------------------
    def log_message(self, fmt, *args):  # 让控制台安静一点
        return

    def send_json(self, data, code: int = 200):
        body = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def send_error_json(self, code: int, message: str):
        self.send_json({"error": message}, code)

    def read_json(self) -> dict:
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length > 2 * 1024 * 1024:
            raise ApiError("请求体过大", 413)
        raw = self.rfile.read(length) if length else b"{}"
        if not raw.strip():
            return {}
        try:
            data = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            raise ApiError("请求内容不是合法的 JSON")
        if not isinstance(data, dict):
            raise ApiError("请求内容必须是 JSON 对象")
        return data

    def authorized(self) -> bool:
        token = self.manager.config.get("auth_token") or ""
        if not token:
            return True
        supplied = self.headers.get("X-Auth-Token") or ""
        if not supplied:
            supplied = (parse_qs(urlparse(self.path).query).get("token") or [""])[0]
        return hmac.compare_digest(str(supplied), str(token))

    # -- 路由 ---------------------------------------------------------------
    def do_GET(self):  # noqa: N802
        try:
            self.route_get()
        except ApiError as exc:
            self.send_error_json(exc.code, str(exc))
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            self.send_error_json(500, f"服务器错误：{exc}")

    def do_POST(self):  # noqa: N802
        try:
            self.route_write("POST")
        except ApiError as exc:
            self.send_error_json(exc.code, str(exc))
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            self.send_error_json(500, f"服务器错误：{exc}")

    def do_PUT(self):  # noqa: N802
        try:
            self.route_write("PUT")
        except ApiError as exc:
            self.send_error_json(exc.code, str(exc))
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            self.send_error_json(500, f"服务器错误：{exc}")

    def do_DELETE(self):  # noqa: N802
        try:
            self.route_write("DELETE")
        except ApiError as exc:
            self.send_error_json(exc.code, str(exc))
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            self.send_error_json(500, f"服务器错误：{exc}")

    def route_get(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if path in STATIC_FILES:
            self.serve_static(*STATIC_FILES[path])
            return
        if not path.startswith("/api/"):
            self.send_error_json(404, "页面不存在")
            return
        if not self.authorized():
            self.send_error_json(401, "需要访问令牌")
            return
        if self.manager.stopping:
            self.send_error_json(503, "管理服务正在重启或停止")
            return

        if path == "/api/health":
            self.send_json({"ok": True, "version": APP_VERSION})
            return
        if path == "/api/state":
            self.send_json(self.manager.snapshot())
            return
        if path == "/api/config":
            with self.manager.lock:
                self.send_json({
                    "port": self.manager.config["port"],
                    "host": self.manager.config["host"],
                    "auth_token": self.manager.config["auth_token"],
                    "startup_delay": self.manager.config["startup_delay"],
                    "extra_path": self.manager.config["extra_path"],
                    "env": self.manager.config["env"],
                    "apps": [a.cfg for a in self.manager.apps.values()],
                })
            return
        if path == "/api/system/logs":
            after = int((query.get("after") or ["0"])[0] or 0)
            items, seq = self.manager.system_log.read(after, 600)
            self.send_json({"lines": items, "next": seq, "file": str(self.manager.system_log.path)})
            return
        if path == "/api/autostart":
            self.send_json(self.manager.autostart_status(force=True))
            return

        m = re.fullmatch(r"/api/apps/([A-Za-z0-9_-]+)/logs", path)
        if m:
            app = self.manager.get_app(m.group(1))
            try:
                after = int((query.get("after") or ["0"])[0] or 0)
            except ValueError:
                after = 0
            try:
                limit = int((query.get("limit") or ["600"])[0] or 600)
            except ValueError:
                limit = 600
            items, seq = app.log.read(after, limit)
            self.send_json({"lines": items, "next": seq, "status": app.snapshot()["status"]})
            return

        m = re.fullmatch(r"/api/apps/([A-Za-z0-9_-]+)/logs/download", path)
        if m:
            app = self.manager.get_app(m.group(1))
            text = app.log.dump_text()
            body = text.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Disposition",
                             f'attachment; filename="{safe_filename(app.id)}.log"')
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return

        self.send_error_json(404, "接口不存在")

    def route_write(self, method: str):
        parsed = urlparse(self.path)
        path = parsed.path
        if not self.authorized():
            self.send_error_json(401, "需要访问令牌")
            return
        if self.manager.stopping:
            self.send_error_json(503, "管理服务正在重启或停止")
            return

        body = self.read_json()

        if method == "POST" and path == "/api/apps":
            raw = body.get("app", body)
            app = self.manager.add_app(raw)
            self.send_json({"ok": True, "app": app}, 201)
            return

        if method == "PUT" and path == "/api/config":
            raw = body.get("config", body)
            result = self.manager.apply_config(raw)
            self.send_json(result)
            return

        if method == "POST" and path == "/api/config/reorder":
            ids = body.get("ids")
            if not isinstance(ids, list):
                raise ApiError("ids 必须是数组")
            self.manager.reorder_apps([str(x) for x in ids])
            self.send_json({"ok": True})
            return

        if method == "POST" and path == "/api/all/start":
            self.send_json({"ok": True, "results": self.manager.start_all()})
            return
        if method == "POST" and path == "/api/all/stop":
            self.manager.stop_all()
            self.send_json({"ok": True})
            return

        if method == "POST" and path == "/api/manager/restart":
            self.manager.stopping = True
            self.send_json({"ok": True})
            self.close_connection = True
            self.manager._close_http()
            threading.Thread(target=self.manager.restart_self, daemon=True,
                             name="restart").start()
            return
        if method == "POST" and path == "/api/manager/shutdown":
            self.manager.stopping = True
            self.send_json({"ok": True})
            self.close_connection = True
            self.manager._close_http()
            threading.Thread(target=self.manager.shutdown, daemon=True,
                             name="shutdown").start()
            return

        if method == "POST" and path == "/api/autostart":
            action = str(body.get("action") or "")
            mode = str(body.get("mode") or "boot")
            self.send_json(self.manager.set_autostart(action, mode))
            return

        m = re.fullmatch(r"/api/apps/([A-Za-z0-9_-]+)", path)
        if m:
            app_id = m.group(1)
            if method == "PUT":
                app = self.manager.update_app(app_id, body.get("app", body))
                self.send_json({"ok": True, "app": app})
                return
            if method == "DELETE":
                self.send_json(self.manager.delete_app(app_id))
                return

        m = re.fullmatch(r"/api/apps/([A-Za-z0-9_-]+)/(start|stop|restart|clear-logs)", path)
        if m and method == "POST":
            app = self.manager.get_app(m.group(1))
            action = m.group(2)
            if action == "start":
                ok, msg = app.manual_start()
                self.send_json({"ok": ok, "message": msg, "app": app.snapshot()})
                return
            if action == "stop":
                ok, msg = app.stop()
                self.send_json({"ok": ok, "message": msg, "app": app.snapshot()})
                return
            if action == "restart":
                app.stop()
                ok, msg = app.manual_start()
                self.send_json({"ok": ok, "message": "已重启" if ok else msg,
                                "app": app.snapshot()})
                return
            if action == "clear-logs":
                app.clear_logs()
                self.send_json({"ok": True, "log_seq": app.log.seq})
                return

        self.send_error_json(404, "接口不存在")

    def serve_static(self, filename: str, content_type: str):
        path = WEB_DIR / filename
        if not path.is_file():
            self.send_error_json(404, "页面文件缺失")
            return
        body = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


# --------------------------------------------------------------------------- #
# 入口
# --------------------------------------------------------------------------- #

def default_config_path() -> Path:
    return ROOT / "config.json"


def main():
    setup_console_encoding()
    parser = argparse.ArgumentParser(description="应用管家 - Windows 应用进程管理面板")
    parser.add_argument("--config", default=str(default_config_path()), help="配置文件路径")
    parser.add_argument("--port", type=int, default=0, help="覆盖配置文件中的端口")
    parser.add_argument("--host", default="", help="覆盖配置文件中的监听地址")
    parser.add_argument("--open", action="store_true", help="启动后自动打开浏览器")
    parser.add_argument("--no-browser", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--version", action="version", version=f"AppManager {APP_VERSION}")
    args = parser.parse_args()

    manager = Manager(Path(args.config).resolve(), args)
    manager.load()

    host = manager.config["host"]
    port = manager.config["port"]

    if instance_running(host, port):
        check_host = "127.0.0.1" if host in ("0.0.0.0", "127.0.0.1", "localhost") else host
        url = f"http://{check_host}:{port}"
        print(f"  应用管家已经在运行  ->  {url}")
        print("  （无需重复启动，已在运行的实例会继续工作）")
        if args.open and not args.no_browser:
            webbrowser.open(url)
        return

    httpd = None
    deadline = time.time() + 20
    last_error = None
    while time.time() < deadline:
        try:
            httpd = AppHTTPServer((host, port), RequestHandler, manager)
            break
        except OSError as exc:
            last_error = exc
            time.sleep(0.5)
    if httpd is None:
        raise SystemExit(f"无法监听 {host}:{port}：{last_error}\n（端口可能被占用）")

    manager.httpd = httpd
    manager.refresh_autostart_async()
    if host == "0.0.0.0":
        display_url = f"http://127.0.0.1:{port}"
        manager.system_log.append(f"[管家] 已监听所有网卡（0.0.0.0:{port}），可从局域网访问")
    else:
        display_url = f"http://{host}:{port}"
        manager.system_log.append(f"[管家] 已启动：{display_url}")

    print(f"  应用管家已启动  ->  {display_url}")
    print(f"  配置文件: {manager.config_path}")
    print(f"  日志目录: {LOGS_DIR}")
    print("  按 Ctrl+C 停止管理服务（会同时停止所有应用）")
    sys.stdout.flush()

    if args.open and not args.no_browser:
        threading.Timer(1.0, lambda: webbrowser.open(display_url)).start()

    def serve_forever():
        try:
            httpd.serve_forever()
        except Exception:  # noqa: BLE001
            pass

    threading.Thread(target=manager.boot_sequence, daemon=True, name="boot").start()
    threading.Thread(target=serve_forever, daemon=True, name="http").start()

    try:
        while not manager.stop_event.wait(0.5):
            pass
    except KeyboardInterrupt:
        print("\n收到退出信号……")
    finally:
        manager.shutdown()
        try:
            httpd.server_close()
        except Exception:  # noqa: BLE001
            pass
        manager.system_log.append("[管家] 已退出")


if __name__ == "__main__":
    main()
