# Giddy Up scenario tests

Scenario tests for the Claude Test Kit (format: `ClaudeTestKit\tests\README.md`). They need a **dev build** of
Giddy Up (the `GiddyUp.TestState` probe only answers in dev builds) and default Giddy Up settings.

Run the whole suite (files run in name order; 02 and 03 depend on that order):

```
/ctk test C:\Users\lordd\Claude\work\GiddyUp\tests
/ctk test report
```

or one file: `/ctk test C:\Users\lordd\Claude\work\GiddyUp\tests\04-action-dismount.json`.

## Common preconditions

- Machinist (or another job with Peloton) at level 20+, out of combat, on foot, not in a duty or PvP.
- Open world where mounts work, e.g. Thavnair (zone 957), with open, flat ground ahead (~60 y) and the camera
  behind the character (the left stick walks "forward" relative to the camera).
- Mount "SDS Fenrir" unlocked (04 and 05 summon it with `/mount`).
- Giddy Up settings at their defaults: Auto Sprint, Keep Peloton, mount after combat ("right away"), dismount on
  action with the action afterwards, dismount on enemy "in range". The tests check this through the probe and fail
  early with a clear message if a feature is off.

## Probe

`GiddyUp.TestState` returns one line, `; `-separated:

```
foot=watching; moving=0.0; mount=none; effectWait=0; ride=off; replay=0; hook=1; range=25 Heated Split Shot; rangeReady=1; cantLand=0; run=6.0(12); sprint=7.8(3); peloton=7.2(2); lag=0.30(8); trip=0; on=sprint,peloton,mount:zone|combat|gather|craft|talk,actionDismount+replay,enemy:inRange
```

| key | meaning |
|---|---|
| `foot` | on-foot watcher (Sprint/Peloton/auto-mount) running (`watching`) or not (`off`: mounted, in combat, between areas, all on-foot features off) |
| `moving` | seconds you've been moving (reacts after `MoveSeconds`, 0.4 s by default) |
| `mount` | pending auto-mount: `none`, or the reason and timing, e.g. `after combat (right away)`, `after talking or interacting (when moving)` |
| `effectWait` | 1 while waiting for a just-used Sprint/Peloton/mount to show up |
| `ride` | enemy watcher while mounted: `off` (not mounted or feature off), `None` (watching, no enemy), `Approaching <gap>/<dismount distance>`, `NoSight`, `WaitingStill`, `OnTrip` |
| `replay` | 1 while an action waits to be used after a dismount |
| `hook` | 1 if the action-use hook is in place (dismount on action works) |
| `range` | attack range in yalms and the action it comes from |
| `rangeReady` | 1 once the range for your job/level is known (the game-data scan runs in the background after loading or a job change; 0 meanwhile = the 3 y fallback) |
| `cantLand` | 1 after the game ignored a flying landing and its re-press: the next Dismount waits until you've moved 1.5 y and hold still |
| `run`/`sprint`/`peloton` | learned speed (y/s) and number of samples (0 = still the default) |
| `lag` | learned action delay (s) and samples |
| `trip` | 1 during a Beeline trip (Beeline then decides mounting) |
| `on` | enabled features |

## Tests

| file | checks | extra preconditions |
|---|---|---|
| `01-probe.json` | the probe answers with every field and the action hook is in place | dev build |
| `02-sprint-on-foot.json` | walking 6 s with the stick: Giddy Up uses Sprint (`[GiddyUp] Sprint (…)`, status 50), no Peloton on top of the fresh Sprint, no dropped press, no ACTION TWICE / WOBBLE | Sprint off cooldown (not used in the last 60 s); the test waits up to 35 s for old Sprint/Peloton buffs to wear off |
| `03-peloton-on-foot.json` | right after 02 (Sprint still on cooldown): waits for Sprint to wear off, walks 6 s, Giddy Up uses Peloton (`[GiddyUp] Peloton`, status 1199) and not Sprint; no ACTION TWICE / WOBBLE | run directly after 02 (needs Sprint on cooldown); a job with Peloton |
| `04-action-dismount.json` | mounts, uses Reassemble with `/ac`: Giddy Up dismounts (`Dismount for Reassemble`), then uses Reassemble once on foot (`Reassemble used after dismounting`), nothing left queued | Machinist, Reassemble has a charge, on foot |
| `05-enemy-dismount-then-mount.json` | mounts, `/targetenemy`: Giddy Up sees the enemy and dismounts in range (`Dismount: enemy within`); after the fight it wants a mount (`Will mount (after combat)`) and summons it (`Mounting (after combat…)`) | exactly one enemy within ~20 y in view (closer than the attack range), nothing else hostile nearby; the fight itself happens the way you usually fight (the test waits up to 120 s for it to end) |

All tests are `strict: false`: unrelated watchdog findings (frame hitches...) don't fail them; the anomalies that
matter are asserted explicitly.

## Manual only

- **Auto-mount after a zone change** (right away): needs a teleport / zone change, which a scenario can't do
  reliably (aetheryte choice, loading screens, mounts not allowed in many arrival zones). Check by hand: teleport
  to an open-world zone, the mount comes after loading (`[GiddyUp] Will mount (zone change)`, then `Mounting (zone change…)`).
- **Auto-mount after talking / interacting, gathering, crafting** (when you start moving): needs an NPC talk, a
  gathering node or a crafting session. Check by hand: finish it, then walk; the probe shows
  `mount=after … (when moving)` until you move, and the mount comes after `MoveSeconds` of moving.
- **Targeting an enemy cancels a pending mount** (`Not mounting (…): you targeted an enemy`): needs a pending
  mount (above) plus a new enemy target at the right moment.
- **Dismount while flying (landing first, second press on touchdown) and "hold still to land"**: needs flying
  toward an enemy and holding still at the right moment; timing-dependent.
- **"Can't land here" notice**: needs a spot where the game refuses the landing (only certain terrain); can't be
  set up from a scenario.
- **Peloton renewal and the Sprint-to-Peloton hand-over** (`Peloton renewed`, `Peloton (taking over from Sprint)`):
  needs 20-30 s of continuous walking, longer than one stick hold and more open ground than a test spot usually has.
- **Beeline link** (no auto-mount during trips, speeds from Beeline): needs Beeline and a trip.
- **Sprint after another plugin's Peloton** (fix eb37f6a): when another plugin fires Peloton as you start
  walking, Giddy Up's Sprint must wait for the animation lock: record a walk-off, expect exactly ONE
  `GeneralAction 4 "Sprint"` request after the DIRECT Peloton, no `Sprint didn't go through`, no ACTION TWICE.
  (02 checks the plain case; this one needs the other plugin to act first.)
- **First enemy right after loading Giddy Up while mounted** (fix 609e2a4): reload Giddy Up while mounted, target
  an enemy: the probe must show `range=27 …` (not the 3 y fallback) and the dismount happens at ~27 y.
  A scenario can't reload a plugin.
- **Hover at ground level, then dismount**: a flying mount hovering just above the floor must get off right
  away (`landed from 0 y`, off in ~0.2 s).
- **Peloton held back** (`Peloton held back: …`) while a mount is pending or before an enemy dismount: needs a
  pending mount or an enemy approach at the right moment.
