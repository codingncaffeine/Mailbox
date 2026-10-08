# pop3.py PORT EXPECTED_PASSWORD LOG — a minimal plain POP3 server for the send/receive gate.
# Logs whether the password it was sent is the expected one (never the password itself).
import socketserver, sys, time
PORT, EXPECTED, LOG = int(sys.argv[1]), sys.argv[2], sys.argv[3]
MSGS = [f"From: Gate <gate@localhost>\r\nTo: you@example.com\r\nSubject: Gate message {i}\r\nMessage-ID: <gate-{i}@localhost>\r\nDate: Thu, 08 Oct 2026 12:0{i}:00 +0000\r\n\r\nBody {i}.\r\n" for i in (1, 2)]
def log(s):
    with open(LOG, "a") as f: f.write(time.strftime("%H:%M:%S ") + s + "\n")
class H(socketserver.StreamRequestHandler):
    def w(self, s): self.wfile.write((s + "\r\n").encode())
    def handle(self):
        log("connect"); self.w("+OK gate ready"); authed = False
        for raw in self.rfile:
            line = raw.decode(errors="replace").rstrip("\r\n"); cmd = line.split(" ", 1)[0].upper(); arg = line[len(cmd) + 1:]
            if cmd == "CAPA": self.w("+OK"); self.w("USER"); self.w("UIDL"); self.w(".")
            elif cmd == "USER": log(f"USER {arg}"); self.w("+OK")
            elif cmd == "PASS":
                if arg == EXPECTED: log("PASS correct"); authed = True; self.w("+OK logged in")
                else: log(f"PASS WRONG (length {len(arg)})"); self.w("-ERR [AUTH] invalid credentials")
            elif not authed and cmd not in ("QUIT",): self.w("-ERR not authenticated")
            elif cmd == "STAT": self.w(f"+OK {len(MSGS)} {sum(len(m) for m in MSGS)}")
            elif cmd == "UIDL":
                if arg: self.w(f"+OK {arg} gate-{arg}")
                else: self.w("+OK"); [self.w(f"{i} gate-{i}") for i in range(1, len(MSGS) + 1)]; self.w(".")
            elif cmd == "LIST":
                if arg: self.w(f"+OK {arg} {len(MSGS[int(arg)-1])}")
                else: self.w("+OK"); [self.w(f"{i} {len(m)}") for i, m in enumerate(MSGS, 1)]; self.w(".")
            elif cmd == "RETR": log(f"RETR {arg}"); self.w(f"+OK {len(MSGS[int(arg)-1])}"); self.wfile.write(MSGS[int(arg)-1].encode()); self.w(".")
            elif cmd == "TOP": self.w("+OK"); self.wfile.write(MSGS[int(arg.split()[0])-1].split("\r\n\r\n")[0].encode() + b"\r\n\r\n"); self.w(".")
            elif cmd == "DELE": self.w("+OK")
            elif cmd == "NOOP": self.w("+OK")
            elif cmd == "QUIT": self.w("+OK bye"); log("quit"); return
            else: self.w("-ERR unknown")
socketserver.ThreadingTCPServer.allow_reuse_address = True
with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), H) as s: s.serve_forever()
