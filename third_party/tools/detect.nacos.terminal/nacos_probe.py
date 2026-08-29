#!/usr/bin/env python3
# coding: utf-8
"""Hackermes-authored read-only Nacos probe for the ToolHost.

Checks a handful of well-known, read-only Nacos exposure issues against one
authorized base URL. Never mutates the target (no user creation, no config
writes, no auth bypass POSTs) — exploitation belongs behind separate,
operator-approved flows.

Output contract (consumed by ReconObservationParser):
  [NACOS-HIT] <check-id> | <severity> | <url> | <detail>
  [NACOS-MISS] <check-id>
  [SUMMARY] probed=<n> hits=<k> errors=<e>
"""
import argparse
import json
import re
import sys

import requests

try:
    import urllib3
    urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
except Exception:
    pass

CHECKS = [
    # (check-id, path, severity, detail keyword must appear in body, json_field_check)
    ("console-exposed", "/nacos/index.html", "info", None),
    ("server-state", "/nacos/v1/console/server/state", "info", None),
    ("user-list-unauth", "/nacos/v1/auth/users?pageNo=1&pageSize=9", "high", "username"),
    ("config-list-unauth", "/nacos/v1/cs/configs?search=accurate&dataId=&group=&pageNo=1&pageSize=99", "high", None),
    ("cluster-node-info", "/nacos/v1/ns/cluster/list", "medium", None),
    ("metrics-exposed", "/nacos/actuator/prometheus", "low", None),
]


def probe(base, timeout):
    hits = 0
    probed = 0
    errors = 0
    session = requests.Session()
    for check_id, path, severity, marker in CHECKS:
        url = base.rstrip("/") + path
        probed += 1
        try:
            response = session.get(url, verify=False, timeout=timeout, allow_redirects=False)
        except requests.RequestException as exc:
            print(f"[NACOS-ERROR] {check_id} | {type(exc).__name__}")
            errors += 1
            continue
        body = response.text or ""
        detail = f"status {response.status_code}"
        is_hit = response.status_code == 200 and len(body) > 0 and "404" not in body[:40]
        if response.status_code == 200 and marker:
            is_hit = marker in body
        if is_hit:
            preview = body[:120].replace("\n", " ").strip()
            print(f"[NACOS-HIT] {check_id} | {severity} | {url} | {detail}; body head: {preview}")
            hits += 1
        else:
            print(f"[NACOS-MISS] {check_id}")
    print(f"[SUMMARY] probed={probed} hits={hits} errors={errors}")


def main():
    parser = argparse.ArgumentParser(description="Hackermes read-only Nacos probe")
    parser.add_argument("--target", required=True, help="authorized base URL, e.g. http://host:8848")
    parser.add_argument("--timeout", type=float, default=5.0)
    args = parser.parse_args()
    base = (args.target or "").strip().rstrip("/")
    if not re.match(r"^https?://(?:[A-Za-z0-9.\-]+|\[[0-9A-Fa-f:]{2,45}\])(?::[0-9]{1,5})?$", base):
        print("[FATAL] target must be an exact http(s) base URL without a path")
        return 2
    probe(base, min(max(args.timeout, 1.0), 10.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
