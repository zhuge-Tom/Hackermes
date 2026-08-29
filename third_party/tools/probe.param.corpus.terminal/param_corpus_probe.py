import sys
import urllib.parse
import urllib.request
import urllib.error

TIMEOUT = 8
MAX_BODY = 60000
DEFAULT_LIMIT = 40


def fetch(url):
    req = urllib.request.Request(url, method="GET", headers={"User-Agent": "hackermes-recon"})
    try:
        with urllib.request.urlopen(req, timeout=TIMEOUT) as resp:
            body = resp.read(MAX_BODY)
            return resp.status, len(body), None
    except urllib.error.HTTPError as exc:
        try:
            length = len(exc.read(200))
        except Exception:
            length = 0
        return exc.code, length, None
    except Exception as exc:
        return 0, 0, "ERR:" + type(exc).__name__


def check(base, param, payload):
    separator = "&" if "?" in base else "?"
    url = base + separator + urllib.parse.urlencode({param: payload})
    return fetch(url)


def main():
    if len(sys.argv) < 5:
        print("usage: param_corpus_probe.py <base_url> <param> <value> <corpus> [limit]", file=sys.stderr)
        return 2
    base, param, value = sys.argv[1], sys.argv[2], sys.argv[3]
    corpus = sys.argv[4]
    limit = int(sys.argv[5]) if len(sys.argv) > 5 else DEFAULT_LIMIT
    try:
        with open(corpus, encoding="utf-8", errors="ignore") as fh:
            payloads = [line.strip() for line in fh if line.strip()][:limit]
    except OSError as exc:
        print("corpus error: %s" % exc, file=sys.stderr)
        return 3

    baseline_status, baseline_len, _ = check(base, param, value)
    for payload in payloads:
        status, length, err = check(base, param, payload)
        trigger = (err is not None and err.startswith("ERR")) or (status != baseline_status) \
            or (baseline_len > 0 and abs(length - baseline_len) > 200)
        if trigger:
            safe = payload if len(payload) <= 80 else payload[:80] + "..."
            print("CANDIDATE param=%s payload=%s status=%s len=%s baseline=%s/%s err=%s"
                  % (param, safe, status, length, baseline_status, baseline_len, err or "-"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
