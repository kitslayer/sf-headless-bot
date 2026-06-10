"""Live 2D viewer for a headless Stick Fight training instance.

Renders the bridge snapshot stream (read-only — zero impact on training):
rigs with HP/aim/armed state, ground weapons, projectiles, the agent's ray
fan, and the void box the env uses. SF's playfield is the YZ plane; we draw
z horizontally (MoveRight = -z = rightward on screen) and y up.

Usage (from the desktop session / Sunshine):
    ~/stickfight-bot/.venv/bin/python python/sf_viewer.py            # instance 0
    ~/stickfight-bot/.venv/bin/python python/sf_viewer.py --port 1342
From ssh/tmux prefix with: DISPLAY=:0

Keys: 1-4 switch instance · r toggle rays · q/esc quit
"""
import argparse
import json
import os
import socket
import time

import pygame

W, H = 1000, 620
SCALE = 20.0
CX, CY = W // 2, H // 2 - 40
VOID_Y = float(os.environ.get("SF_VOID_Y", "-11.5"))
VOID_Z = float(os.environ.get("SF_VOID_Z", "19.0"))

GREEN = (80, 220, 100)
RED = (235, 90, 80)
YELLOW = (240, 210, 60)
GRAY = (120, 120, 130)
DIM = (60, 60, 70)
WHITE = (235, 235, 235)
BLUE = (90, 160, 255)
BG = (18, 18, 24)


def w2s(z, y):
    """World (z right-negative, y up) → screen px."""
    return int(CX - z * SCALE), int(CY - y * SCALE)


def snap_once(sock, port):
    try:
        sock.sendto(b'{"cmd":"snapshot"}', ("127.0.0.1", port))
        data, _ = sock.recvfrom(65535)
        return json.loads(data.decode(errors="replace"))
    except (socket.timeout, ValueError, OSError):
        return None


def draw_rig(screen, font, e, color, show_rays):
    z, y = float(e.get("z", 0)), float(e.get("y", 0))
    px, py = w2s(z, y)
    alive = e.get("alive", False)
    armed = e.get("armed", False)

    if show_rays:
        rays = e.get("rays") or []
        for i, r in enumerate(rays[:16]):
            ang = i * 0.3926991
            import math
            dz, dy = math.cos(ang), math.sin(ang)
            d = float(r) * 20.0
            ex, ey = w2s(z + dz * d, y + dy * d)
            pygame.draw.line(screen, DIM, (px, py), (ex, ey), 1)

    if not alive:
        pygame.draw.line(screen, GRAY, (px - 8, py - 8), (px + 8, py + 8), 3)
        pygame.draw.line(screen, GRAY, (px - 8, py + 8), (px + 8, py - 8), 3)
        return

    # velocity (thin), aim (thick)
    vz, vy = float(e.get("vz", 0)), float(e.get("vy", 0))
    pygame.draw.line(screen, DIM, (px, py), w2s(z + vz * 0.4, y + vy * 0.4), 2)
    az, ay = float(e.get("aimz", 0)), float(e.get("aimy", 0))
    pygame.draw.line(screen, WHITE, (px, py), w2s(z + az * 2.2, y + ay * 2.2), 3)

    pygame.draw.circle(screen, color, (px, py), 9)
    if armed:
        pygame.draw.circle(screen, YELLOW, (px, py), 13, 3)
    if e.get("blk"):
        pygame.draw.circle(screen, BLUE, (px, py), 16, 2)

    hp = max(0.0, min(100.0, float(e.get("hp", 0))))
    bw = 36
    pygame.draw.rect(screen, (50, 50, 55), (px - bw // 2, py - 26, bw, 6))
    pygame.draw.rect(screen, color, (px - bw // 2, py - 26, int(bw * hp / 100.0), 6))
    lbl = font.render(f"{int(hp)}", True, WHITE)
    screen.blit(lbl, (px - lbl.get_width() // 2, py - 42))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=1341, help="bridge port (1341-1344)")
    ap.add_argument("--hz", type=float, default=30.0)
    args = ap.parse_args()
    port = args.port

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(0.25)

    pygame.init()
    screen = pygame.display.set_mode((W, H))
    pygame.display.set_caption(f"SF headless viewer — bridge {port}")
    font = pygame.font.SysFont("monospace", 14)
    big = pygame.font.SysFont("monospace", 20, bold=True)
    clock = pygame.time.Clock()

    show_rays = True
    last_snap, last_ok = None, 0.0

    while True:
        for ev in pygame.event.get():
            if ev.type == pygame.QUIT:
                return
            if ev.type == pygame.KEYDOWN:
                if ev.key in (pygame.K_q, pygame.K_ESCAPE):
                    return
                if ev.key == pygame.K_r:
                    show_rays = not show_rays
                for k, p in ((pygame.K_1, 1341), (pygame.K_2, 1342),
                             (pygame.K_3, 1343), (pygame.K_4, 1344)):
                    if ev.key == k:
                        port = p
                        pygame.display.set_caption(f"SF headless viewer — bridge {port}")

        snap = snap_once(sock, port)
        if snap and snap.get("reply") == "snapshot":
            last_snap, last_ok = snap, time.time()
        snap = last_snap

        screen.fill(BG)
        # void box (the env's kill bounds) + kill-floor line
        x0, y0 = w2s(VOID_Z, 12.0)
        x1, y1 = w2s(-VOID_Z, VOID_Y)
        pygame.draw.rect(screen, (90, 40, 40), (x0, y0, x1 - x0, y1 - y0), 2)
        kx0, ky = w2s(VOID_Z, VOID_Y)
        kx1, _ = w2s(-VOID_Z, VOID_Y)
        pygame.draw.line(screen, RED, (kx0, ky), (kx1, ky), 2)

        if snap:
            for wp in (snap.get("wps") or []):
                if len(wp) >= 2:
                    px, py = w2s(float(wp[0]), float(wp[1]))
                    pygame.draw.rect(screen, YELLOW, (px - 5, py - 5, 10, 10))
            for pr in (snap.get("proj") or []):
                if len(pr) >= 4:
                    px, py = w2s(float(pr[0]), float(pr[1]))
                    pygame.draw.circle(screen, WHITE, (px, py), 3)
                    ex, ey = w2s(float(pr[0]) + float(pr[2]) * 1.5,
                                 float(pr[1]) + float(pr[3]) * 1.5)
                    pygame.draw.line(screen, WHITE, (px, py), (ex, ey), 1)
            for e in snap.get("ents", []):
                color = GREEN if e.get("slot") == 0 else RED
                draw_rig(screen, font, e, color, show_rays and e.get("slot") == 0)

            hud = (f"bridge {port}  scene={snap.get('scene','?')}  "
                   f"round={snap.get('round','?')}  tick={snap.get('tick','?')}  "
                   f"inFight={snap.get('inFight')}  wps={len(snap.get('wps') or [])}")
            screen.blit(font.render(hud, True, WHITE), (10, 8))
            legend = "green=agent  red=opp  yellow ring=armed  yellow box=weapon  white line=aim  [1-4] instance  [r] rays  [q] quit"
            screen.blit(font.render(legend, True, GRAY), (10, H - 24))

        if time.time() - last_ok > 1.5:
            msg = big.render(f"RECONNECTING to 127.0.0.1:{port} ...", True, YELLOW)
            screen.blit(msg, (W // 2 - msg.get_width() // 2, H // 2))

        pygame.display.flip()
        clock.tick(args.hz)


if __name__ == "__main__":
    main()
