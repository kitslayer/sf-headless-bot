# Status / Architecture Notes

_Living doc for the headless bot arena. Updated as the system evolves._

## Current state

- **Single-instance scripted self-play**: 2 bots spawn at map spawn points,
  fight (melee), die, and the round advances to a new map — using the stock SF
  lifecycle (no teleport/clamp hacks).
- **Round loop**: bots are destroyed on round advance (canonical clears
  `SlotToRig`), then re-spawned on the next map. `AutoSpawnBots` re-arms each
  round via `_botAutoSpawnDone` reset in `ResetOracleStateForRoundAdvance`.
- **Self-heal**: if a scene can't host 2 bots (e.g. non-combat maps), the
  round auto-advances to find a playable one. `LevelEditor` (scene 103) is
  hard-excluded via `SF_EXCLUDE_MAPS`.

## Key mechanisms (all 1:1 with stock SF where it matters)

| Concern | Approach |
|---|---|
| Spawn position | `currentMapInfo.spawnPoints[slot].localPosition` |
| Rig setup | `ConfigureBotRig`: `mHasControl`, `SetCollision(true)`, Standing/Fighting |
| Health init | `GameManager.RevivePlayer(ctrl, true)` |
| Placement | stock private `MovePlayer` coroutine via reflection |
| Kinematic | network-match `MovePlayer` leaves rigs kinematic → we flip dynamic |
| Death | network `Die()` throws on Steamworks → set `isDead` + drop weapon locally |
| Round end | canonical `TickAuthRigDeathCheck` sees `isDead` → schedules advance |

## Scaling

- Each instance ≈ 0.7–1 GB RAM, ~1–2 cores. Host has 24 cores / 15 GB
  (shared with docker media stacks). Target ~6 instances.
- `scripts/fleet.sh start N` — N instances on ports 1337..1337+N-1, isolated
  wineprefixes under `prefixes/`. `scripts/watchdog.sh` restarts crashed/stalled
  instances.

## TODO / roadmap

- [ ] RL training hookup (task: wire PPO to the fleet). Plan: extend the JSON
      bridge (port 1341+i) with `setBotAction` (write `SlotInputs[slot]`) and use
      the existing `snapshot` command for observations. Python Gym env →
      self-play PPO, reward = Δopp_HP − Δself_HP. No external client.
- [ ] Curate a known-good combat-map whitelist (vs. relying on self-heal to
      skip bad scenes) for cleaner training distribution.
- [ ] Per-instance metrics (rounds/min, mean episode length) for monitoring.
