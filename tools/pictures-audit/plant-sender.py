# plant-sender.py STORE_DB SUBJECT NEW_ADDRESS — files a copy of the message with that subject, from another sender.
import sqlite3, sys, zlib
db, subject, address = sys.argv[1:4]
con = sqlite3.connect(db)
row = con.execute("SELECT m.id, m.folder_id, b.bytes, b.compression FROM messages m JOIN blobs b ON b.id = m.blob_id "
                  "WHERE m.subject = ? ORDER BY m.id DESC LIMIT 1", (subject,)).fetchone()
mid, folder, data, kind = row
raw = zlib.decompress(data, -15) if kind == "deflate" else data
text = raw.decode("utf-8", "surrogateescape")
assert "stranger@example.net" in text
text = text.replace("stranger@example.net", address).replace("A. Stranger", "B. Other")
out = text.encode("utf-8", "surrogateescape")
packer = zlib.compressobj(9, zlib.DEFLATED, -15)
packed = packer.compress(out) + packer.flush()
cur = con.execute("INSERT INTO blobs (bytes, byte_length, compression) VALUES (?, ?, ?)",
                  (packed if kind == "deflate" else out, len(out), kind))
blob = cur.lastrowid
con.execute("INSERT INTO messages (folder_id, blob_id, server_uid, message_id, from_name, from_address, subject, preview, "
            "received_utc, size_bytes, is_read, body_text) "
            "SELECT folder_id, ?, server_uid || '-other', message_id || '.other', 'B. Other', ?, subject || ' (other sender)', preview, "
            "received_utc, size_bytes, is_read, body_text FROM messages WHERE id = ?", (blob, address, mid))
con.commit()
print(con.execute("SELECT subject FROM messages WHERE blob_id = ?", (blob,)).fetchone()[0])
