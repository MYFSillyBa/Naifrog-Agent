"""Loopback-only deterministic function-tool model for real WPF acceptance. No forwarding."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import time

output = Path(__file__).resolve().parent.parent / "output"
output.mkdir(exist_ok=True)

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        record = dict(path=self.path, method="GET", fake_key_ok=self.headers.get("Authorization") == "Bearer FAKE-AGENT-UI")
        with (output / "agent-ui-models.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record) + "\n")
        payload = json.dumps(dict(data=[dict(id="agent-ui-fixture"), dict(id="agent-ui-alternate"), dict(id="embedding-fixture")])).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(payload))); self.end_headers(); self.wfile.write(payload)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        messages = body.get("messages", [])
        tool_results = [m for m in messages if m.get("role") == "tool"]
        if any("CANCEL_AGENT_HTTP" in str(m.get("content", "")) for m in messages if m.get("role") == "user"):
            time.sleep(6)
        calls = []
        text = ""
        if len(tool_results) == 0:
            name, args = "list_files", {"path": "."}
        elif len(tool_results) == 1:
            name, args = "read_file", {"path": "sample.txt"}
        elif len(tool_results) == 2:
            result = json.loads(tool_results[-1]["content"])
            name, args = "replace_text", {"path": "sample.txt", "old_text": "before", "new_text": "after", "expected_hash": result.get("sha256", result.get("hash"))}
        elif len(tool_results) == 3:
            name, args = "run_command", {"command": "Get-Content -LiteralPath 'sample.txt'", "working_directory": "."}
        else:
            name = None
            text = "工程任务已完成：sample.txt 已修改，终端验证读取到 after。"
            if any("USER_REFERENCE_UI" in str(m.get("content", "")) for m in messages if m.get("role") == "user"):
                text += " 补充引用已纳入，原任务继续完成。"
        if name:
            calls = [dict(id=f"ui_call_{len(tool_results)}", type="function", function=dict(name=name, arguments=json.dumps(args)))]
        message = dict(role="assistant", content=text or None)
        if calls:
            message["tool_calls"] = calls
        record = dict(path=self.path, model=body.get("model"), fake_key_ok=self.headers.get("Authorization") == "Bearer FAKE-AGENT-UI", tools=len(body.get("tools", [])), tool_results=tool_results, references=[m.get("content") for m in messages if m.get("role") == "user" and "USER_REFERENCE_UI" in str(m.get("content", ""))])
        with (output / "agent-ui-requests.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, ensure_ascii=False) + "\n")
        payload = json.dumps(dict(choices=[dict(message=message, finish_reason="tool_calls" if calls else "stop")]), ensure_ascii=False).encode("utf-8")
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
(output / "agent-ui-port.txt").write_text(str(server.server_port), encoding="utf-8")
print(f"Agent UI fixture ready: {server.server_port}", flush=True)
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()
