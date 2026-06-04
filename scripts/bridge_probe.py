#!/usr/bin/env python3
"""Probe the SFHeadlessHost JSON bridge (loopback UDP).

Validates the RL hookup:
  - `snapshot` returns enriched obs (hp/alive/vx,vy,vz/armed + inFight/round)
  - `setBotAction` is accepted (ack ok:true)

Usage: bridge_probe.py [BRIDGE_PORT]   (default 1341 = instance 0)
"""
import json
import socket
import sys
import time

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 1341
ADDR = ("127.0.0.1", PORT)


def rpc(sock, obj, wait=0.5):
    sock.sendto(json.dumps(obj).encode(), ADDR)
    sock.settimeout(wait)
    try:
        data, _ = sock.recvfrom(65535)
        return data.decode(errors="replace")
    except socket.timeout:
        return None


def main():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind(("127.0.0.1", 0))  # ephemeral local port; bridge replies to sender

    print(f"== ping :{PORT} ==")
    print(" ", rpc(s, {"cmd": "ping"}))

    print("== snapshot (enriched obs) ==")
    snap = rpc(s, {"cmd": "snapshot"})
    print(" ", snap)
    if snap:
        try:
            j = json.loads(snap)
            print(f"   parsed: inFight={j.get('inFight')} round={j.get('round')} "
                  f"scene={j.get('scene')} ents={len(j.get('ents', []))}")
            for e in j.get("ents", []):
                print(f"     slot {e['slot']}: hp={e.get('hp')} alive={e.get('alive')} "
                      f"armed={e.get('armed')} pos=({e['x']},{e['y']},{e['z']}) "
                      f"vel=({e.get('vx')},{e.get('vy')},{e.get('vz')})")
        except Exception as ex:
            print("   parse error:", ex)

    print("== setBotAction slot 0 (walk +mx, fire) ==")
    print(" ", rpc(s, {"cmd": "setBotAction", "slot": 0,
                       "mx": 1.0, "my": 0.0, "aimx": -1.0, "aimy": 0.0, "buttons": 2}))

    s.close()


if __name__ == "__main__":
    main()
