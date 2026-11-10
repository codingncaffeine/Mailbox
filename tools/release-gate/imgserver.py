# imgserver.py PORT DIR LOG — images that fail the ways real servers make them fail.
import http.server, sys, time, os
PORT, DIR, LOG = int(sys.argv[1]), sys.argv[2], sys.argv[3]
PNG = open(os.path.join(DIR, "picture.png"), "rb").read()
def bmp():
    # An 18 MB uncompressed picture, the size of a newsletter's animated header: what a cap on
    # picture size refuses. 2600x2400, 24-bit, gradient so it is visibly a picture.
    w, h = 2600, 2400; row = w * 3; size = 54 + row * h
    hdr = b"BM" + size.to_bytes(4, "little") + b"\0\0\0\0" + (54).to_bytes(4, "little")
    hdr += (40).to_bytes(4, "little") + w.to_bytes(4, "little") + h.to_bytes(4, "little") + (1).to_bytes(2, "little") + (24).to_bytes(2, "little") + b"\0" * 24
    rows = bytearray()
    for y in range(h):
        rows += bytes([y * 255 // h, 90, 200]) * w
    return hdr + bytes(rows)
BIG = None
class H(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, ctype, body):
        self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        with open(LOG, "a") as f: f.write(f"{time.strftime('%H:%M:%S')} {self.path} {code}\n")
    def do_GET(self):
        p = self.path
        if p.startswith("/octet"): self.send(200, "application/octet-stream", PNG)
        elif p.startswith("/forbidden"): self.send(403, "text/html", b"<h1>Forbidden</h1>")
        elif p.startswith("/page"): self.send(200, "text/html", b"<html><body>not an image</body></html>")
        elif p.startswith("/slow"): time.sleep(2); self.send(200, "image/png", PNG)
        elif p.startswith("/big"):
            global BIG
            if BIG is None: BIG = bmp()
            self.send(200, "application/octet-stream", BIG)
        else: self.send(200, "image/png", PNG)
http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
