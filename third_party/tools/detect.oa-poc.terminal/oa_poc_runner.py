#!/usr/bin/env python3
# coding: utf-8
"""Single-shot OA-EXPTOOL POC runner for the Hackermes ToolHost.

Reimplements the bounded subset of OA-EXPTOOL's yaml engine (nuclei-style
word/status matchers plus request-index extractors) so one authorized target
can be probed against one OA module's POC set without the interactive
MSF-style console. Print-only output; never writes files and never runs the
exploitation payloads bundled elsewhere in upstream tooling.

Usage:
  python oa_poc_runner.py --list
  python oa_poc_runner.py --target http://host:port --module tongda [--poc file.yaml] [--timeout 6]
"""
import argparse
import os
import re
import sys

import requests
import yaml

try:
    import urllib3
    urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
except Exception:
    pass

BOOK_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "book")
KNOWN_SEVERITIES = {"critical", "high", "medium", "low", "info"}
REQUEST_TIMEOUT_CAP = 10.0


def safe_name(value, limit=120):
    value = (value or "").strip()
    if not value or len(value) > limit or any(character in value for character in "/\\"):
        return None
    return value


def load_poc(module, filename):
    path = os.path.join(BOOK_ROOT, module, filename)
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = yaml.safe_load(handle.read())
    except Exception as exc:
        return ("__error__", f"yaml parse failed: {exc}")
    if not isinstance(data, dict) or not isinstance(data.get("http"), list) or not data["http"]:
        return None
    info = data.get("info") or {}
    severity = str(info.get("severity", "info")).strip().lower()
    if severity not in KNOWN_SEVERITIES:
        severity = "info"
    name = str(info.get("name") or data.get("id") or filename).strip()
    return (name, severity, data["http"][0])


def as_list(value):
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def header_dict(raw):
    headers = {}
    if not isinstance(raw, str) or raw.strip().lower() == "none":
        return headers
    for chunk in raw.split("&"):
        chunk = chunk.strip()
        if ":" not in chunk:
            continue
        key, val = chunk.split(":", 1)
        headers[key.strip()] = val.strip()
    return headers


def apply_extractors(extractors, index, response, buckets):
    """Apply request-index extractors, substituting results into future request strings.

    `buckets` is a list of the mutable request-string lists (paths, bodies, rheaders,
    gheaders); substitution happens in place so later iterations see updated values.
    """
    for extractor in extractors or []:
        if not isinstance(extractor, dict):
            continue
        times = as_list(extractor.get("time"))
        if index not in times:
            continue
        names = as_list(extractor.get("name"))
        parts = as_list(extractor.get("part"))
        regexes = as_list(extractor.get("regex"))
        for position, pattern in enumerate(regexes):
            part = str(parts[position]) if position < len(parts) else "body"
            name = str(names[position]) if position < len(names) else None
            if not name:
                continue
            try:
                if part in ("Gheader", "Rheader"):
                    # Raw "K: V" lines (py2 httplib format) so line-anchored regexes do
                    # not swallow the neighbouring dict-noise of str(response.headers).
                    haystack = "\r\n".join(f"{key}: {value}" for key, value in response.headers.items())
                else:
                    haystack = response.text or ""
                match = re.search(pattern, haystack)
                result = match.group(1) if match and match.groups() else (match.group(0) if match else "")
            except re.error:
                continue
            result = (result or "").strip()
            for bucket in buckets:
                bucket[:] = [item.replace(name, result) for item in bucket]


def evaluate_matchers(matchers, condition, response):
    results = []
    for matcher in matchers or []:
        if not isinstance(matcher, dict):
            continue
        matcher_type = str(matcher.get("type", "")).lower()
        part = str(matcher.get("part", "body")).lower()
        matcher_condition = str(matcher.get("condition", "or")).lower() == "and"
        if matcher_type == "status":
            results.append(response.status_code in as_list(matcher.get("status")))
        elif matcher_type == "word":
            words = [str(word) for word in as_list(matcher.get("words"))]
            if part == "header":
                haystack = [str(value).lower() for value in response.headers.values()]
                checks = [any(word.lower() in value for value in haystack) for word in words]
            else:
                body = response.text or ""
                checks = [word in body for word in words]
            results.append(all(checks) if matcher_condition else any(checks))
        elif matcher_type == "regex":
            body = response.text or ""
            checks = [re.search(str(pattern), body) is not None for pattern in as_list(matcher.get("regex"))]
            results.append(all(checks) if matcher_condition else any(checks))
        else:
            results.append(False)
    if not results:
        return False
    return all(results) if str(condition).lower() == "and" else any(results)


def run_poc(base, poc, request_timeout):
    http = poc[2]
    methods = [str(method).upper() for method in as_list(http.get("method")) or ["GET"]]
    paths = [str(path).replace("{{BaseURL}}", "") for path in as_list(http.get("path"))]
    bodies = [str(body) for body in as_list(http.get("body"))]
    rheaders = [str(item) for item in as_list(http.get("Rheader"))]
    gheaders = [str(item) for item in as_list(http.get("Gheader"))]
    extractors = http.get("extractors")
    # The four request-string lists are mutated in place by apply_extractors so that
    # every iteration (paths, bodies and both header families) sees extracted values.
    buckets = [paths, bodies, rheaders, gheaders]

    response = None
    last_url = base
    post_cursor = 0
    get_cursor = 0
    for index in range(len(paths)):
        path = paths[index]
        method = methods[index] if index < len(methods) else "GET"
        url = base + path
        # Upstream indexes Gheader per GET request and Rheader per POST request
        # (each counter resets when the POC declares "None"), not by overall index.
        headers = {}
        if method == "POST":
            body = bodies[post_cursor] if post_cursor < len(bodies) else ""
            rheader = rheaders[post_cursor] if post_cursor < len(rheaders) else "None"
            if rheader.strip().lower() == "none":
                post_cursor = 0
            else:
                headers.update(header_dict(rheader))
                post_cursor += 1
            response = requests.post(url, headers=headers, data=body.encode("utf-8"),
                                     verify=False, timeout=request_timeout, allow_redirects=False)
        else:
            gheader = gheaders[get_cursor] if get_cursor < len(gheaders) else "None"
            if gheader.strip().lower() == "none":
                get_cursor = 0
            else:
                headers.update(header_dict(gheader))
                get_cursor += 1
            response = requests.get(url, headers=headers, verify=False,
                                    timeout=request_timeout, allow_redirects=False)
        last_url = url
        apply_extractors(extractors, index + 1, response, buckets)
    if response is None:
        return None
    condition = http.get("matchers-condition", "and")
    return evaluate_matchers(http.get("matchers"), condition, response), last_url


def list_pocs():
    count = 0
    for module in sorted(os.listdir(BOOK_ROOT)):
        module_path = os.path.join(BOOK_ROOT, module)
        if not os.path.isdir(module_path):
            continue
        for filename in sorted(os.listdir(module_path)):
            if not filename.endswith(".yaml"):
                continue
            poc = load_poc(module, filename)
            if poc and len(poc) == 3:
                print(f"[POC] {module}/{filename} | {poc[0]} | {poc[1]}")
                count += 1
    print(f"[SUMMARY] listing complete pocs={count}")


def probe(base, module, poc_name, request_timeout):
    module = safe_name(module, 64)
    module_path = os.path.join(BOOK_ROOT, module or "")
    if not module or not os.path.isdir(module_path):
        print(f"[FATAL] unknown module '{module}'")
        return 2
    filenames = sorted(os.listdir(module_path))
    if poc_name:
        poc_name = safe_name(poc_name)
        if not poc_name or poc_name not in filenames:
            print(f"[FATAL] unknown poc '{poc_name}' in module '{module}'")
            return 2
        filenames = [poc_name]
    probed = hits = errors = 0
    for filename in filenames:
        if not filename.endswith(".yaml"):
            continue
        poc = load_poc(module, filename)
        if poc is None:
            continue
        if len(poc) == 2:
            print(f"[ERROR] {filename} | {poc[1]}")
            errors += 1
            continue
        probed += 1
        try:
            verdict = run_poc(base, poc, request_timeout)
        except requests.RequestException as exc:
            reason = str(exc).split("\n", 1)[0][:200]
            print(f"[ERROR] {poc[0]} | request failed: {type(exc).__name__}: {reason}")
            errors += 1
            continue
        if verdict is None:
            print(f"[MISS] {poc[0]} (no response)")
        elif verdict[0]:
            print(f"[HIT] {poc[0]} | {poc[1]} | {verdict[1]}")
            hits += 1
        else:
            print(f"[MISS] {poc[0]}")
    print(f"[SUMMARY] module={module} probed={probed} hits={hits} errors={errors}")
    return 0


def main():
    parser = argparse.ArgumentParser(description="Hackermes single-shot OA POC runner")
    parser.add_argument("--list", action="store_true", help="list bundled POCs and exit")
    parser.add_argument("--target", help="authorized base URL, e.g. http://host:port")
    parser.add_argument("--module", help="POC module (book subdirectory), e.g. tongda")
    parser.add_argument("--poc", help="optional single POC yaml filename within the module")
    parser.add_argument("--timeout", type=float, default=6.0, help="per-request timeout in seconds")
    args = parser.parse_args()
    if args.list:
        list_pocs()
        return 0
    base = (args.target or "").strip().rstrip("/")
    if not re.match(r"^https?://(?:[A-Za-z0-9.\-]+|\[[0-9A-Fa-f:]{2,45}\])(?::[0-9]{1,5})?$", base):
        print("[FATAL] target must be an exact http(s) base URL without a path")
        return 2
    return probe(base, args.module, args.poc, min(max(args.timeout, 1.0), REQUEST_TIMEOUT_CAP))


if __name__ == "__main__":
    sys.exit(main())
