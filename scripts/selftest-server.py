#!/usr/bin/env python3
"""Dependency-free loopback HTTP server for Hookmes desktop acceptance tests."""

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Handler(BaseHTTPRequestHandler):
    page: bytes

    def do_GET(self):
        if self.path.split("?", 1)[0] == "/selftest-page.html":
            self._send(200, "text/html; charset=utf-8", self.page)
        elif self.path == "/health":
            self._send(200, "text/plain", b"ok")
        elif self.path.split("?", 1)[0].startswith("/api/"):
            self._api(b"")
        else:
            self._send(404, "text/plain", b"not found")

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        self._api(self.rfile.read(length))

    def _api(self, body: bytes):
        payload = json.dumps({"path": self.path.split("?", 1)[0], "body": body.decode("utf-8")}).encode()
        self._send(200, "application/json; charset=utf-8", payload)

    def _send(self, status: int, content_type: str, body: bytes):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        return


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--page", type=Path, required=True)
    args = parser.parse_args()
    Handler.page = args.page.read_bytes()
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
