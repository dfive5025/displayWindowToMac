"""Turn an Annex B H.264/HEVC file (encoded with AUDs) into OpenDisplay wire frames.

Each output record is [4-byte big-endian length][payload], exactly what a sender
puts on the socket. AUD NAL units are dropped, every start code becomes the
4-byte form, and each payload gets the optional telemetry prefix.

    ffmpeg -f lavfi -i testsrc=size=320x240:rate=30 -frames:v 30 -c:v libx264 \
        -bf 0 -g 300 -x264-params aud=1:repeat-headers=1 -pix_fmt yuv420p -f h264 in.h264
    python3 tools/make_fixture.py in.h264 out.odframes
"""
import re
import struct
import sys
import time


def split_nals(data: bytes):
    starts = [m.start() for m in re.finditer(b"\x00\x00\x01", data)]
    nals = []
    for i, s in enumerate(starts):
        end = starts[i + 1] if i + 1 < len(starts) else len(data)
        nal = data[s + 3:end]
        # A 4-byte start code leaves a trailing zero on the previous NAL.
        if i + 1 < len(starts) and end > 0 and data[end - 1] == 0:
            nal = data[s + 3:end - 1]
        nals.append(nal)
    return nals


def main(src: str, dst: str, hevc: bool = False):
    nals = split_nals(open(src, "rb").read())
    is_aud = (lambda n: ((n[0] >> 1) & 0x3F) == 35) if hevc else (lambda n: (n[0] & 0x1F) == 9)
    units, cur = [], []
    for nal in nals:
        if not nal:
            continue
        if is_aud(nal):
            if cur:
                units.append(cur)
            cur = []
        else:
            cur.append(nal)
    if cur:
        units.append(cur)
    with open(dst, "wb") as out:
        for unit in units:
            now = int(time.time() * 1000)
            payload = b'{"cap":%d,"snd":%d}' % (now, now) + b"".join(b"\x00\x00\x00\x01" + n for n in unit)
            out.write(struct.pack(">I", len(payload)) + payload)
    print(f"{len(units)} access units -> {dst}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], hevc="--hevc" in sys.argv)
