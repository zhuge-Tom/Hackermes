import socket
import sys
import concurrent.futures

TIMEOUT = 2
MAX_WORKERS = 32
MAX_HITS = 256


def resolve(name):
    try:
        infos = socket.getaddrinfo(name, 0, socket.AF_INET, socket.SOCK_STREAM)
        return name, infos[0][4][0]
    except Exception:
        return name, None


def main():
    if len(sys.argv) < 3:
        print("usage: subdomain_enum.py <domain> <wordlist>", file=sys.stderr)
        return 2
    domain = sys.argv[1]
    wordlist = sys.argv[2]
    candidates = []
    try:
        with open(wordlist, encoding="utf-8", errors="ignore") as fh:
            for raw in fh:
                line = raw.strip()
                if not line or line.startswith("#"):
                    continue
                line = line.strip("*.")
                if line.startswith("."):
                    line = line[1:]
                candidates.append(f"{line}.{domain}".lower())
    except OSError as exc:
        print(f"wordlist error: {exc}", file=sys.stderr)
        return 3

    hits = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=MAX_WORKERS) as pool:
        for name, ip in pool.map(resolve, candidates):
            if ip:
                hits.append((name, ip))
    for name, ip in hits[:MAX_HITS]:
        print(f"{name} -> {ip}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
