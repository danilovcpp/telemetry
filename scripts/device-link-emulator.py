#!/usr/bin/env python3
"""Device Link v1 emulator: an SG Manager-like device for testing the server without hardware.

Connects to /api/device/v1/link with a device code and key, sends hello, answers
  live.start / live.stop  - values of two channels (sine waves) every periodMs while the 60 s lease holds
  logs.months / logs.days - from the samples folder
  file.read               - bytes of the sample files, at most 4 KB per answer (like the firmware)
and behaves like the firmware where it deviates from the specification: the X-Device-Id header, atUtc to the second,
extra fields "unknown" and "complete", error codes bad_request and not_supported.

Samples folder layout (device path = path relative to the folder):
  <samples>/ch<N>/<YYYY-MM>/<DD>.TSV     ch0 is the summary file of all channels
Files are in the device format: '#' header lines, tab-separated, decimal comma, CRLF, -99999,50 for a missing value.

  pip install websockets
  python scripts/device-link-emulator.py --make-samples scripts/samples
  python scripts/device-link-emulator.py --device-id stg-device-002 --key dtk_... --samples scripts/samples
  python scripts/device-link-emulator.py --url wss://telemetry.example.com/api/device/v1/link ... --grow 60

The key can also come from the DEVICE_KEY environment variable. Never use the key of a real device.
"""

import argparse
import asyncio
import base64
import datetime as dt
import json
import math
import os
import pathlib
import random
import sys
import time

MAX_CHUNK = 4096        # the firmware answers file.read with at most 4 KB
MAX_LENGTH = 8192       # protocol maximum of file.read length
LEASE_S = 60
CHANNELS = [
    {"id": "ch1", "name": "Temperature", "unit": "°C", "type": "analog"},
    {"id": "ch2", "name": "Pressure", "unit": "bar", "type": "analog"},
]


def utc_now():
    return dt.datetime.now(dt.timezone.utc)


def iso_seconds(moment):
    return moment.replace(microsecond=0).strftime("%Y-%m-%dT%H:%M:%SZ")


def decimal_comma(value):
    return f"{value:.2f}".replace(".", ",")


def sample_line(moment, channels):
    values = []
    for number in channels:
        if random.random() < 0.02:
            values.append("-99999,50")          # placeholder for a missing value
        else:
            minutes = moment.hour * 60 + moment.minute
            values.append(decimal_comma(20 + 5 * number + 3 * math.sin(minutes / 1440 * 2 * math.pi * number)))
    return moment.strftime("%Y-%m-%d\t%H:%M:%S") + "\t" + "\t".join(values) + "\r\n"


def header(channel):
    names = [c["name"] for c in CHANNELS] if channel == 0 else [CHANNELS[channel - 1]["name"]]
    return ("# SG Manager emulator\r\n"
            f"# channel {channel}\r\n"
            "# date\ttime\t" + "\t".join(names) + "\r\n")


def channel_numbers(channel):
    return list(range(1, len(CHANNELS) + 1)) if channel == 0 else [channel]


def make_samples(root, days, step_minutes):
    """Writes the last `days` days (today included, up to now) for ch0..chN."""
    root = pathlib.Path(root)
    now = utc_now()
    for channel in range(0, len(CHANNELS) + 1):
        for back in range(days - 1, -1, -1):
            day = (now - dt.timedelta(days=back)).date()
            folder = root / f"ch{channel}" / day.strftime("%Y-%m")
            folder.mkdir(parents=True, exist_ok=True)
            moment = dt.datetime(day.year, day.month, day.day, tzinfo=dt.timezone.utc)
            end = min(moment + dt.timedelta(days=1), now)
            lines = [header(channel)]
            while moment < end:
                lines.append(sample_line(moment, channel_numbers(channel)))
                moment += dt.timedelta(minutes=step_minutes)
            (folder / f"{day.day:02d}.TSV").write_bytes("".join(lines).encode("latin-1"))
    print(f"samples written to {root}")


class Device:
    def __init__(self, args):
        self.args = args
        self.root = pathlib.Path(args.samples).resolve() if args.samples else None
        self.socket = None
        self.send_lock = asyncio.Lock()
        self.lease_until = 0.0
        self.period_ms = 1000
        self.live_task = None
        self.started = time.monotonic()
        self.boot_id = f"{random.getrandbits(32):08x}"

    async def send(self, message):
        async with self.send_lock:
            await self.socket.send(json.dumps(message, ensure_ascii=False))

    def hello(self):
        offset = dt.datetime.now().astimezone().utcoffset()
        return {
            "op": "hello",
            "link": 1,
            "schema": 1,
            "fw": "emulator-1.0",
            "bootId": self.boot_id,
            "chipUid": "EMU-" + self.args.device_id,
            "clockValid": True,
            "utcOffsetMin": int(offset.total_seconds() // 60) if offset else 0,
            "tzName": self.args.tz,
            "caps": [c for c in self.args.caps.split(",") if c],
            "channels": CHANNELS,
        }

    # --- live ---------------------------------------------------------------------------------------------------

    async def live_loop(self):
        tick = 0
        while time.monotonic() < self.lease_until:
            tick += 1
            t = int((time.monotonic() - self.started) * 1000)
            phase = tick * self.period_ms / 1000
            await self.send({
                "op": "values",
                "t": t,
                "atUtc": iso_seconds(utc_now()),
                "values": [
                    {"id": "ch1", "t": t, "value": round(21 + 4 * math.sin(phase / 10), 2)},
                    {"id": "ch2", "t": t, "value": round(1.5 + 0.3 * math.sin(phase / 7), 3)},
                ],
            })
            await asyncio.sleep(self.period_ms / 1000)
        print("live: lease expired")

    async def live_start(self, message):
        if "live" not in self.args.caps.split(","):
            return await self.send({"op": "error", "for": "live.start", "code": "not_supported"})
        period = message.get("periodMs", 1000)
        if not isinstance(period, int) or not 1000 <= period <= 10000:
            return await self.send({"op": "error", "for": "live.start", "code": "bad_request"})
        self.period_ms = period
        self.lease_until = time.monotonic() + LEASE_S
        if self.live_task is None or self.live_task.done():
            print(f"live: start, every {period} ms")
            self.live_task = asyncio.create_task(self.live_loop())

    def live_stop(self):
        print("live: stop")
        self.lease_until = 0

    # --- files --------------------------------------------------------------------------------------------------

    def resolve(self, path):
        if self.root is None or not isinstance(path, str) or "\\" in path:
            return None
        full = (self.root / path.lstrip("/")).resolve()
        return full if self.root in full.parents else None

    async def error(self, request, code, **extra):
        await self.send({"op": "error", "for": request.get("op"), "reqId": request.get("reqId"), "code": code, **extra})

    async def logs_months(self, request):
        ch = request.get("ch")
        if not isinstance(ch, int) or ch < 0:
            return await self.error(request, "bad_request")
        folder = self.root / f"ch{ch}" if self.root else None
        months = sorted(p.name for p in folder.iterdir() if p.is_dir()) if folder and folder.is_dir() else []
        await self.send({"op": "logs.months", "reqId": request.get("reqId"), "months": months, "unknown": True})

    async def logs_days(self, request):
        ch, month = request.get("ch"), request.get("month")
        if not isinstance(ch, int) or not isinstance(month, str):
            return await self.error(request, "bad_request")
        folder = self.root / f"ch{ch}" / month if self.root else None
        if folder is None or not folder.is_dir():
            return await self.error(request, "not_found")
        today = utc_now().strftime("%Y-%m-%d")
        days = []
        for file in sorted(folder.glob("*.TSV")):
            closed = f"{month}-{file.stem}" < today
            days.append({
                "path": file.relative_to(self.root).as_posix(),
                "size": file.stat().st_size,
                "closed": closed,
                "complete": closed,
            })
        await self.send({"op": "logs.days", "reqId": request.get("reqId"), "days": days})

    async def file_read(self, request):
        offset, length = request.get("offset"), request.get("length")
        if not isinstance(offset, int) or not isinstance(length, int) or offset < 0 or not 0 < length <= MAX_LENGTH:
            return await self.error(request, "bad_request")
        full = self.resolve(request.get("path"))
        if full is None:
            return await self.error(request, "bad_path")
        if not full.is_file():
            return await self.error(request, "not_found")
        size = full.stat().st_size
        with open(full, "rb") as stream:
            stream.seek(min(offset, size))
            data = stream.read(min(length, MAX_CHUNK))
        await self.send({
            "op": "file.data",
            "reqId": request.get("reqId"),
            "offset": offset,
            "size": size,
            "data": base64.b64encode(data).decode("ascii"),
            "eof": offset + len(data) >= size,
        })

    async def grow(self):
        """Appends a line to today's files every --grow seconds (an open day that keeps growing)."""
        while True:
            await asyncio.sleep(self.args.grow)
            now = utc_now()
            for channel in range(0, len(CHANNELS) + 1):
                folder = self.root / f"ch{channel}" / now.strftime("%Y-%m")
                folder.mkdir(parents=True, exist_ok=True)
                file = folder / f"{now.day:02d}.TSV"
                prefix = "" if file.exists() else header(channel)
                with open(file, "ab") as stream:
                    stream.write((prefix + sample_line(now, channel_numbers(channel))).encode("latin-1"))

    # --- connection ---------------------------------------------------------------------------------------------

    async def handle(self, message):
        op = message.get("op")
        if op == "welcome":
            print(f"welcome (link {message.get('link')})")
        elif op == "live.start":
            await self.live_start(message)
        elif op == "live.stop":
            self.live_stop()
        elif op == "logs.months":
            await self.logs_months(message)
        elif op == "logs.days":
            await self.logs_days(message)
        elif op == "file.read":
            await self.file_read(message)
        else:
            print(f"unknown op {op!r}")
            await self.error(message, "not_supported")

    async def run(self):
        import websockets

        headers = {"X-Device-Id": self.args.device_id, "X-Device-Key": self.args.key}
        try:
            connect = websockets.connect(self.args.url, additional_headers=headers, max_size=16 * 1024)
        except TypeError:   # websockets < 14
            connect = websockets.connect(self.args.url, extra_headers=headers, max_size=16 * 1024)

        async with connect as socket:
            self.socket = socket
            print(f"connected to {self.args.url} as {self.args.device_id}")
            await self.send(self.hello())
            grower = asyncio.create_task(self.grow()) if self.args.grow and self.root else None
            try:
                async for frame in socket:
                    # One file.read at a time on the server side, but answer without blocking live requests.
                    asyncio.create_task(self.handle(json.loads(frame)))
            finally:
                if grower:
                    grower.cancel()
            print(f"closed: {socket.close_code} {socket.close_reason}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default="ws://localhost:5146/api/device/v1/link")
    parser.add_argument("--device-id", help="device code (sent as X-Device-Id)")
    parser.add_argument("--key", default=os.environ.get("DEVICE_KEY"), help="device API key (or DEVICE_KEY)")
    parser.add_argument("--samples", help="samples folder served by logs.* and file.read")
    parser.add_argument("--caps", default="live,files", help="capabilities in hello, comma-separated")
    parser.add_argument("--tz", default="Europe/Berlin", help="tzName in hello")
    parser.add_argument("--grow", type=int, default=0, help="append a line to today's files every N seconds")
    parser.add_argument("--make-samples", metavar="DIR", help="write sample files to DIR and exit")
    parser.add_argument("--days", type=int, default=45, help="days of samples (--make-samples)")
    parser.add_argument("--step", type=int, default=10, help="minutes between sample lines (--make-samples)")
    parser.add_argument("--reconnect", action="store_true", help="reconnect after the connection ends")
    args = parser.parse_args()

    if args.make_samples:
        make_samples(args.make_samples, args.days, args.step)
        return
    if not args.device_id or not args.key:
        parser.error("--device-id and --key (or DEVICE_KEY) are required")

    while True:
        try:
            asyncio.run(Device(args).run())
        except KeyboardInterrupt:
            return
        except Exception as error:      # noqa: BLE001 - report and maybe reconnect
            print(f"connection failed: {error}", file=sys.stderr)
        if not args.reconnect:
            return
        time.sleep(5)


if __name__ == "__main__":
    main()
