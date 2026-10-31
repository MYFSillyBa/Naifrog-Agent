"""Local-only model fixture for real desktop UI acceptance checks. No forwarding."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import time

root = Path(__file__).resolve().parent.parent / "output"
root.mkdir(exist_ok=True)
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        messages = body.get("messages", [])
        last = messages[-1].get("content", "") if messages else ""
        slow = isinstance(last, str) and "CANCEL_UI_TEST" in last
        content = "本机模拟连接成功。" if not body.get("stream") else "收到，你的 API 配置与自定义提示词已传到本机模拟服务。"
        event = dict(path=self.path, model=body.get("model"), system=messages[0].get("content") if messages else None,
                     authorization=self.headers.get("Authorization"), streaming=body.get("stream"), slow=slow)
        with (root / "ui-model-requests.jsonl").open("a", encoding="utf-8") as output:
            output.write(json.dumps(event, ensure_ascii=False) + "\n")
        try:
            if body.get("stream"):
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream; charset=utf-8")
                self.end_headers()
                for part in (["开始等待", "这段不应进入记忆"] if slow else ["收到，", "你的 API 配置与自定义提示词", "已传到本机模拟服务。"]):
                    if slow:
                        time.sleep(2)
                    payload = json.dumps(dict(choices=[dict(delta=dict(content=part))]), ensure_ascii=False)
                    self.wfile.write(("data: " + payload + "\n\n").encode("utf-8")); self.wfile.flush()
                    time.sleep(0.15)
                self.wfile.write(b'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n')
            else:
                payload = json.dumps(dict(choices=[dict(message=dict(content=content), finish_reason="stop")]), ensure_ascii=False).encode("utf-8")
                self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(payload))); self.end_headers(); self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
(root / "ui-model-port.txt").write_text(str(server.server_port), encoding="utf-8")
print("Local model fixture ready:", server.server_port, flush=True)
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
