import os
import sys
import time

print("ticker 启动，中文测试", flush=True)
print(f"PID={os.getpid()}", flush=True)
print(f"PORT={os.environ.get('PORT', '未设置')}", flush=True)

i = 0
while True:
    i += 1
    print(f"tick {i} 时间戳={time.time():.3f}", flush=True)
    if i % 5 == 0:
        print("这是错误流的中文测试", file=sys.stderr, flush=True)
    time.sleep(0.35)
