"""Local HTTP fixture test of the real management executable's download behavior."""
import http.server
import pathlib
import subprocess
import tempfile
import threading

root = pathlib.Path(__file__).resolve().parent.parent
worker = root / "deploy/win-x64/Applications/Worker/Cerebral.Worker.exe"
if not worker.is_file():
    raise SystemExit("Build the Windows Worker deployment before running this test.")
payload = b"MZ" + b"fixture installer data" * 24
counts = {}


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        counts[self.path] = counts.get(self.path, 0) + 1
        if self.path == "/missing":
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        incomplete = self.path == "/broken" or (
            self.path == "/retry" and counts[self.path] == 1
        )
        self.wfile.write(payload[:20] if incomplete else payload)
        self.close_connection = True

    def log_message(self, *args):
        pass


runtime = root / ".runtime"
runtime.mkdir(exist_ok=True)
server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    with tempfile.TemporaryDirectory(prefix="download-test-", dir=runtime) as temporary:
        destination = pathlib.Path(temporary) / "current.exe"

        def download(route):
            return subprocess.run(
                [
                    str(worker), "manage", "download-installer",
                    "--url", f"http://127.0.0.1:{server.server_port}/{route}",
                    "--destination", str(destination),
                ], capture_output=True, text=True, timeout=45,
            )

        result = download("retry")
        assert result.returncode == 0, result.stderr
        assert counts["/retry"] == 2, counts
        assert destination.read_bytes() == payload
        prior = b"existing complete installer remains intact"
        destination.write_bytes(prior)
        result = download("broken")
        assert result.returncode != 0, result.stdout
        assert counts["/broken"] == 3, counts
        assert destination.read_bytes() == prior
        assert not destination.with_suffix(".exe.partial").exists()
        result = download("missing")
        assert result.returncode != 0, result.stdout
        assert counts["/missing"] == 1, counts
        assert destination.read_bytes() == prior
        print("PASS: HTTP fixture truncation/retry, failed-download preservation, partial cleanup, and no retry on 404.")
finally:
    server.shutdown()
    server.server_close()
