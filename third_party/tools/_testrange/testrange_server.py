#!/usr/bin/env python3
# coding: utf-8
"""Hackermes authorized-assessment test range (stage-3 regression fixture).

Serves deterministic mock "vulnerable" targets so the ToolHost adapters can be
verified end to end without external networks:

  /general/userinfo.php?UID=1   POST  -> Tongda session disclosure response
  /seeyon/thirdpartyController.do POST -> issues JSESSIONID=RANGE7C42 (extractor source)
  /seeyon/main.do               GET   -> matching body only when the extracted
                                         session value (RANGE7C42) is replayed
  /v2/api-docs + /api/*         GET   -> swagger document and live endpoints
  /<gitroot>                    GET   -> dumb-HTTP git repository (created by the
                                         test fixture with the real git CLI)

Usage: python testrange_server.py --port 18300 [--gitroot C:/path/repo]
"""
import argparse
import json
import mimetypes
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SESSION_MARKER = "RANGE7C42"
GIT_ROOT = None


class RangeHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"

    def _send(self, status, body, content_type="text/plain; charset=utf-8", extra=None):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        for key, value in (extra or {}).items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(data)

    def _json(self, payload, status=200):
        self._send(status, json.dumps(payload, separators=(",", ":"), ensure_ascii=False),
                   "application/json")

    def do_POST(self):
        if self.path.startswith("/general/userinfo.php"):
            self._json({"dept_name": "测试部门", "online_flag": "1", "real_name": "admin"})
            return
        if self.path.startswith("/seeyon/thirdpartyController.do"):
            self._send(200, "ok", extra={"Set-Cookie": f"JSESSIONID={SESSION_MARKER}; Path=/; HttpOnly"})
            return
        self._send(404, "not found")

    def do_GET(self):
        if self.path.startswith("/seeyon/main.do"):
            cookie = self.headers.get("Cookie", "")
            # Strict: the replayed cookie must contain the extracted session value,
            # proving the extractor chain actually substituted into the next request.
            if SESSION_MARKER in cookie:
                self._send(200, "当前已登录了一个用户，同一窗口中不能登录多个用户 "
                                "<a href='/seeyon/main.do?method=logout'>logout</a>")
            else:
                self._send(200, "no-session-here")
            return
        if self.path.startswith("/v2/api-docs"):
            self._json({"swagger": "2.0", "info": {"title": "range-api", "version": "1.0"},
                        "basePath": "/", "paths": {
                            "/api/users": {"get": {"operationId": "listUsers", "responses": {"200": {}}}},
                            "/api/login": {"post": {"operationId": "login", "responses": {"200": {}},
                                                    "parameters": [{"name": "body", "in": "body",
                                                                    "schema": {"type": "object"}}]}}}})
            return
        if self.path.startswith("/api/users"):
            self._json([{"id": 1, "name": "range-user"}])
            return
        if self.path.startswith("/api/login"):
            self._send(200, "login page")
            return
        if self.path in ("/", "") or self.path.startswith("/?"):
            self._send(200, "<html><body>range index</body></html>", "text/html")
            return
        if GIT_ROOT and self._serve_git():
            return
        self._send(404, "not found")

    def _serve_git(self):
        request_path = self.path.split("?", 1)[0].split("#", 1)[0]
        relative = request_path.lstrip("/")
        if "/.git" not in request_path or ".." in relative:
            return False
        candidate = os.path.normpath(os.path.join(GIT_ROOT, relative.replace("/", os.sep)))
        root = os.path.normpath(GIT_ROOT)
        if not candidate.startswith(root + os.sep) and candidate != root:
            return False
        if not os.path.isfile(candidate):
            self._send(404, "not found")
            return True
        content = open(candidate, "rb").read()
        self._send(200, content, mimetypes.guess_type(candidate)[0] or "application/octet-stream")
        return True

    def log_message(self, *args):
        pass


def main():
    global GIT_ROOT
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=0,
                        help="listening port; defaults to an ephemeral port (avoids bind races)")
    parser.add_argument("--gitroot", default=None)
    args = parser.parse_args()
    GIT_ROOT = os.path.abspath(args.gitroot) if args.gitroot else None
    server = ThreadingHTTPServer(("127.0.0.1", args.port), RangeHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"RANGE_READY port={server.server_address[1]}", flush=True)
    try:
        threading.Event().wait()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
