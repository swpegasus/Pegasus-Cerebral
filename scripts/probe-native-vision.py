"""Opt-in local vision protocol probe. Owns only its child and in-memory test image."""
import argparse, base64, json, socket, struct, subprocess, time, urllib.request, zlib

def image():
    # Two labelled-by-position color regions; no text is embedded in the fixture.
    width, height = 256, 256
    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            color = (230, 25, 25) if 24 <= x < 112 and 40 <= y < 216 else (25, 50, 230) if 144 <= x < 232 and 40 <= y < 216 else (250, 250, 250)
            row.extend(color)
        rows.append(b"\0" + row)
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(b"".join(rows))) + chunk(b"IEND", b"")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--projector", required=True)
    args = parser.parse_args()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    base = f"http://127.0.0.1:{port}/"
    def call(path, body=None, timeout=120):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(base + path, data=data, headers={"Content-Type":"application/json"})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = response.read(1024 * 1024 + 1)
            if len(payload) > 1024 * 1024:
                raise RuntimeError("Oversized native protocol response")
            return json.loads(payload)
    with __import__("tempfile").TemporaryFile(mode="w+b") as log:
        process = subprocess.Popen([args.server, "--model", args.model, "--mmproj", args.projector,
            "--host", "127.0.0.1", "--port", str(port), "--ctx-size", "8192", "--parallel", "1",
            "--n-gpu-layers", "auto", "--fit", "on", "--fit-target", "1024", "--no-context-shift"],
            stdin=subprocess.DEVNULL, stdout=log, stderr=log, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError("Owned llama-server exited before readiness")
                try:
                    if call("health", timeout=2).get("status") == "ok": break
                except OSError: pass
                time.sleep(.25)
            else: raise TimeoutError("Model readiness exceeded three minutes")
            info = call("v1/models")
            # Bounded metadata only; image and prompt are intentionally not printed.
            print("MODEL " + json.dumps(info), flush=True)
            encoded = base64.b64encode(image()).decode()
            messages = [{"role":"system","content":"Describe only what you can see in the supplied image. Be concise."},
                {"role":"user","content":[{"type":"text","text":"Which color is on the left, and which color is on the right?"},
                    {"type":"image_url","image_url":{"url":"data:image/png;base64," + encoded}}]}]
            rendered = call("apply-template", {"messages":messages,"add_generation_prompt":True,"chat_template_kwargs":{"enable_thinking":False}})
            prompt = rendered["prompt"]
            print("TEMPLATE " + json.dumps({"characters":len(prompt),"mediaMarkers":prompt.count("<__media__>"),"fields":list(rendered)}), flush=True)
            native = {"prompt":{"prompt_string":prompt,"multimodal_data":[encoded]}, "id_slot":0,
                "n_predict":0, "stream":False, "cache_prompt":True,
                "response_fields":["content","id_slot","stop","stop_type","tokens_evaluated","tokens_predicted","truncated"]}
            measured = call("v1/chat/completions/input_tokens", {"messages":messages,"chat_template_kwargs":{"enable_thinking":False}})
            print("MEASURE " + json.dumps(measured), flush=True)
            native["n_predict"] = 128
            native["temperature"] = 0
            result = call("completion",native)
            print("GENERATE " + json.dumps(result), flush=True)
            if measured.get("object") != "response.input_tokens" or measured.get("input_tokens",0) <= 0:
                raise RuntimeError("Native token counting did not return an input measurement")
            if result.get("tokens_evaluated") != measured["input_tokens"] or result.get("truncated"):
                raise RuntimeError("Generated prompt count differs from measured input")
            if "red" not in result.get("content","").lower() or "blue" not in result.get("content","").lower():
                raise RuntimeError("Model did not identify the visual fixture colors")
            print("VISION PROTOCOL PROBE PASSED (synthetic image; not native desktop acceptance)", flush=True)
        except Exception:
            log.seek(0,2); size=log.tell(); log.seek(max(0,size-6000))
            print("RUNTIME TAIL " + log.read().decode(errors="replace"),flush=True)
            raise
        finally:
            if process.poll() is None:
                process.terminate()
                try: process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait(timeout=10)
            print("OWNED RUNTIME STOPPED",flush=True)

if __name__ == "__main__":
    main()
