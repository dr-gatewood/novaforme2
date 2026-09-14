#!/usr/bin/env bash
# Builds NTFS test images with ntfs-3g (Linux only). Output: <outdir>/plain.img, gpt.img, damaged-boot.img, damaged-gpt.img, manifest.txt
set -euo pipefail
OUT="${1:-/tmp/nova4me2-testimages}"
SIZE_MB="${2:-160}"
mkdir -p "$OUT"
IMG="$OUT/plain.img"
MNT="$OUT/mnt"
rm -f "$IMG"; mkdir -p "$MNT"
truncate -s "${SIZE_MB}M" "$IMG"
mkfs.ntfs -F -f -Q -L "NovaTest" -c 4096 "$IMG" >/dev/null
ntfs-3g -o streams_interface=windows,compression "$IMG" "$MNT"
cleanup() { fusermount -u "$MNT" 2>/dev/null || umount "$MNT" 2>/dev/null || true; }
trap cleanup EXIT
cd "$MNT"
mkdir -p "Users/Alice/Documents/Projects/deep/deeper/deepest" "Users/Bob/Pictures" "Windows/System32" "Program Files/App" "big"
echo "hello world" > "Users/Alice/Documents/hello.txt"
printf '' > "Users/Alice/Documents/empty.txt"
head -c 700 /dev/urandom > "Users/Alice/Documents/resident-ish.bin"
head -c 5000 /dev/urandom > "Users/Alice/Documents/nonresident-small.bin"
head -c $((20*1024*1024)) /dev/urandom > "big/random-20MiB.bin"
python3 - <<'PY'
import os
# Many files in one directory to force $INDEX_ALLOCATION with several blocks.
os.makedirs("Users/Bob/Pictures/many", exist_ok=True)
for i in range(600):
    with open(f"Users/Bob/Pictures/many/photo_{i:04d}_with_a_reasonably_long_file_name.jpg", "wb") as f:
        f.write(bytes([i % 256]) * (i * 37 % 9000))
# Unicode names
with open("Users/Alice/Documents/Projects/日本語ファイル.txt", "w", encoding="utf-8") as f: f.write("こんにちは")
with open("Users/Alice/Documents/Projects/Ünïcödé — name.md", "w") as f: f.write("# md\n")
# Sparse file: 12 MiB with two written islands.
with open("big/sparse-12MiB.bin", "wb") as f:
    f.truncate(12*1024*1024)
    f.seek(1*1024*1024); f.write(b"A"*4096)
    f.seek(9*1024*1024+123); f.write(os.urandom(70000))
# Fragmented file: fill the volume with 8 KiB files, free every other one, then write one file into the holes.
os.makedirs("big/holes", exist_ok=True)
i = 0
try:
    while True:
        with open(f"big/holes/h{i:05d}", "wb") as f: f.write(os.urandom(8192))
        i += 1
except OSError:
    pass
os.sync()
count = i
for j in range(0, count, 2):
    try: os.remove(f"big/holes/h{j:05d}")
    except OSError: pass
os.sync()
with open("big/frag-a.bin", "wb") as f:
    f.write(os.urandom(150*8192 + 12345)); f.flush(); os.fsync(f.fileno())
# Free most of the fillers again so later steps have room.
for j in range(1, count, 2):
    if j % 8 != 1:
        try: os.remove(f"big/holes/h{j:05d}")
        except OSError: pass
os.sync()
PY
# Compressed directory (ntfs-3g honours FILE_ATTRIBUTE_COMPRESSED inheritance when mounted with -o compression)
mkdir -p "Users/Alice/Compressed"
python3 -c "import os,struct; os.setxattr('Users/Alice/Compressed','system.ntfs_attrib',struct.pack('<I',0x810))"
python3 - <<'PY'
import os
os.makedirs("Users/Alice/Compressed", exist_ok=True)
# Highly compressible + partially compressible + incompressible in a compressed folder.
with open("Users/Alice/Compressed/zeros-3MiB.bin","wb") as f: f.write(b"\0"*(3*1024*1024))
with open("Users/Alice/Compressed/text-2MiB.txt","wb") as f:
    for i in range(40000): f.write(f"line {i:06d} the quick brown fox jumps over the lazy dog\n".encode())
with open("Users/Alice/Compressed/mixed-1MiB.bin","wb") as f:
    for i in range(16): f.write(os.urandom(32768) if i%2 else b"x"*32768)
with open("Users/Alice/Compressed/random-1MiB.bin","wb") as f: f.write(os.urandom(1024*1024))
with open("Users/Alice/Compressed/small.txt","w") as f: f.write("small compressed file\n")
import struct
for n in os.listdir("Users/Alice/Compressed"):
    p = os.path.join("Users/Alice/Compressed", n)
    a = struct.unpack("<I", os.getxattr(p, "system.ntfs_attrib"))[0]
    if not a & 0x800:
        # File was not created compressed; set the flag explicitly and rewrite so ntfs-3g compresses it.
        data = open(p, "rb").read(); os.remove(p)
        open(p, "wb").close(); os.setxattr(p, "system.ntfs_attrib", struct.pack("<I", 0x820))
        with open(p, "wb") as f: f.write(data)
PY
# ---- forensic samples (stdlib python only) ----
python3 - <<'PY'
import os, zlib, struct, base64, zipfile, random, io
os.makedirs("forensics", exist_ok=True)
JPEG = base64.b64decode("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7Pj4+JS5ESUM8SDc9Pjv/2wBDAQoLCw4NDhwQEBw7KCIoOzs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozs7Ozv/wAARCAAQABADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDNooor6o6D/9k=")
PDF = b"%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n" + b"hidden document payload " * 40 + b"\ntrailer << /Root 1 0 R >>\n%%EOF\n"
def png(width, height, rows, extra_chunks=b""):
    def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    raw = b"".join(b"\x00" + r for r in rows)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) + extra_chunks + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
random.seed(42)
W, H = 96, 64
rows = [bytes((x * 255 // W, y * 255 // H, (x + y) * 255 // (W + H)) [c] for x in range(W) for c in range(3)) for y in range(H)]
# 1. JPEG with an appended PDF (copy /b photo.jpg + secret.pdf)
open("forensics/photo.jpg", "wb").write(JPEG + PDF)
open("forensics/photo-clean.jpg", "wb").write(JPEG)
# 2. PNG with a tEXt chunk and a ZIP appended after IEND
zbuf = io.BytesIO()
with zipfile.ZipFile(zbuf, "w", zipfile.ZIP_DEFLATED) as z: z.writestr("secret/notes.txt", "the zip hidden after IEND\n" * 50)
open("forensics/image.png", "wb").write(png(W, H, rows, chunk(b"tEXt", b"Comment\x00made by nova test")) + zbuf.getvalue())
open("forensics/archive.zip", "wb").write(zbuf.getvalue())
# 3. BMP clean vs LSB-embedded (random LSBs across the whole image)
def bmp(pixels_rows):
    stride = (W * 3 + 3) & ~3
    body = b"".join(bytes(r) + b"\x00" * (stride - W * 3) for r in reversed(pixels_rows))
    hdr = b"BM" + struct.pack("<IHHI", 54 + len(body), 0, 0, 54) + struct.pack("<IiiHHIIiiII", 40, W, H, 1, 24, 0, len(body), 2835, 2835, 0, 0)
    return hdr + body
smooth = [[(x * 255 // W + y) % 256 if c == 0 else (y * 255 // H) if c == 1 else ((x * 3 + y * 5) // 4) % 256 for x in range(W) for c in range(3)] for y in range(H)]
# make the clean image "natural": pairs (2k,2k+1) uneven — a gradient already is; add mild noise biased to even values
clean = [[(v & 0xFE) if random.random() < 0.8 else v for v in row] for row in smooth]
stego = [[(v & 0xFE) | random.getrandbits(1) for v in row] for row in clean]
open("forensics/clean.bmp", "wb").write(bmp(clean))
open("forensics/stego.bmp", "wb").write(bmp(stego))
# 4. plain document + ADS host
open("forensics/doc.pdf", "wb").write(PDF)
open("forensics/ads-host.txt", "w").write("visible text\n")
hidden_png = png(8, 8, [bytes([i * 30 % 256, j * 30 % 256, 7] * 8) for i, j in [(k, k) for k in range(8)]][:8])
open("forensics/ads-host.txt:hidden.png", "wb").write(hidden_png)
open("forensics/hidden.expected", "wb").write(hidden_png)
open("forensics/ads-host.txt:Zone.Identifier", "w").write("[ZoneTransfer]\r\nZoneId=3\r\n")
PY
# 5. deleted PNG for unallocated-space carving (written last so its clusters stay free)
python3 - <<'PY'
import os, zlib, struct, random
random.seed(7)
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
W, H = 128, 96
rows = [bytes(random.getrandbits(8) for _ in range(W * 3)) for _ in range(H)]
raw = b"".join(b"\x00" + r for r in rows)
data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
open("forensics/deleted-photo.png", "wb").write(data)
os.sync()
open("../deleted-photo.expected", "wb").write(data)
PY
sync
# Alternate data stream
echo "ads payload" > "Users/Alice/Documents/hello.txt:secret"
# Hard link
ln "Users/Alice/Documents/hello.txt" "Users/Bob/hello-link.txt"
# Deleted file (should be discoverable by MFT scan)
head -c 30000 /dev/urandom > "Users/Bob/deleted-me.bin"
sync
cp "Users/Bob/deleted-me.bin" "$OUT/deleted-me.expected"
rm "Users/Bob/deleted-me.bin"
sync
rm -f "forensics/deleted-photo.png"
sync
# Manifest: relative path + sha256 (files only), excluding ADS.
( cd "$MNT" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$OUT/manifest.txt"
( cd "$MNT" && find . -type d | sort ) > "$OUT/dirs.txt"
cd /
cleanup
trap - EXIT
# Backup boot sector damaged primary variant
cp "$IMG" "$OUT/damaged-boot.img"
dd if=/dev/zero of="$OUT/damaged-boot.img" bs=512 count=1 conv=notrunc status=none
# GPT disk image with the NTFS image as partition 2 (partition 1 = fake EFI, 32 MiB)
python3 - "$IMG" "$OUT/gpt.img" <<'PY'
import sys, struct, uuid, zlib, os
src, dst = sys.argv[1], sys.argv[2]
ss = 512
ntfs = os.path.getsize(src)
efi_start = 1024*1024; efi_len = 32*1024*1024
p2_start = efi_start + efi_len
total = p2_start + ntfs + 1024*1024  # room for backup GPT
total_lba = total // ss
with open(dst, "wb") as f:
    f.truncate(total)
    # protective MBR
    mbr = bytearray(512); mbr[446] = 0; mbr[446+4] = 0xEE
    struct.pack_into("<II", mbr, 446+8, 1, min(total_lba-1, 0xFFFFFFFF)); mbr[510:512] = b"\x55\xAA"
    f.seek(0); f.write(mbr)
    def entry(tguid, start, end, name):
        e = bytearray(128)
        e[0:16] = uuid.UUID(tguid).bytes_le; e[16:32] = uuid.uuid4().bytes_le
        struct.pack_into("<QQQ", e, 32, start, end, 0); e[56:56+len(name)*2] = name.encode("utf-16-le"); return e
    entries = bytearray(128*128)
    entries[0:128] = entry("C12A7328-F81F-11D2-BA4B-00A0C93EC93B", efi_start//ss, (efi_start+efi_len)//ss - 1, "EFI system partition")
    entries[128:256] = entry("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", p2_start//ss, (p2_start+ntfs)//ss - 1, "Basic data partition")
    ecrc = zlib.crc32(bytes(entries)) & 0xffffffff
    disk_guid = uuid.uuid4().bytes_le
    def header(my, alt, elba):
        h = bytearray(512)
        h[0:8] = b"EFI PART"; struct.pack_into("<IIII", h, 8, 0x00010000, 92, 0, 0)
        struct.pack_into("<QQQQ", h, 24, my, alt, 34, total_lba-34)
        h[56:72] = disk_guid; struct.pack_into("<QIII", h, 72, elba, 128, 128, ecrc)
        struct.pack_into("<I", h, 16, zlib.crc32(bytes(h[:92])) & 0xffffffff); return h
    f.seek(ss); f.write(header(1, total_lba-1, 2)); f.seek(2*ss); f.write(entries)
    f.seek((total_lba-33)*ss); f.write(entries); f.seek((total_lba-1)*ss); f.write(header(total_lba-1, 1, total_lba-33))
    with open(src, "rb") as s:
        f.seek(p2_start)
        while True:
            b = s.read(4*1024*1024)
            if not b: break
            f.write(b)
PY
cp "$OUT/gpt.img" "$OUT/damaged-gpt.img"
dd if=/dev/zero of="$OUT/damaged-gpt.img" bs=512 seek=1 count=33 conv=notrunc status=none
echo "Images written to $OUT"; ls -la "$OUT"
