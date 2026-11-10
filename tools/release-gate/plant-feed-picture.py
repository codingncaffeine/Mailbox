# plant-feed-picture.py FEEDS_DB URL — gives the newest article in the store a picture at URL.
# The feeds module must load an article's pictures without a press while mail's are held back;
# the gate opens this article and checks the picture arrived.
import sqlite3, sys, zlib, email
from email import policy
db, url = sys.argv[1], sys.argv[2]
con = sqlite3.connect(db)
mid, blob, data, kind = con.execute(
    "SELECT m.id, b.id, b.bytes, b.compression FROM messages m JOIN blobs b ON b.id = m.blob_id "
    "ORDER BY m.received_utc DESC LIMIT 1").fetchone()
raw = zlib.decompress(data, -15) if kind == "deflate" else data
msg = email.message_from_bytes(raw, policy=policy.default)
html = f'<html><body><p>Gate feed article.</p><img src="{url}" width="60" height="30"></body></html>'
msg.set_content(html, subtype="html")
out = msg.as_bytes()
packed = zlib.compressobj(9, zlib.DEFLATED, -15)
packed = packed.compress(out) + packed.flush()
con.execute("UPDATE blobs SET bytes = ?, byte_length = ? WHERE id = ?",
            (packed if kind == "deflate" else out, len(out), blob))
con.commit()
print(con.execute("SELECT subject FROM messages WHERE id = ?", (mid,)).fetchone()[0])
