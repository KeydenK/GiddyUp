<p align="center">
  <img src="GiddyUp/images/icon.png" width="128" alt="Giddy Up icon">
</p>

<h1 align="center">Giddy Up</h1>

<p align="center">
  A Dalamud plugin for FINAL FANTASY XIV: <b>mounting, dismounting, Sprint and Peloton</b><br>
  done for you, <b>at the right moment</b>.
</p>

---

## Why?

Out in the world you spend a lot of button presses on getting around: summon the mount after every fight, get off before
you can attack, press Sprint, keep Peloton up... Giddy Up does all of that for you, and times it from your own measured
speeds so it never gets in your way.

## Features

- **Mounts you up** where mounts are allowed (never in duties; field operations like Eureka, Bozja and Occult
  Crescent count as open world), after:
  - a **zone change or teleport** and a **fight**: right away,
  - **gathering, crafting, talking to someone or using something**: as soon as you start moving (so a node, the
    crafting log or a quest giver with several quests doesn't get a mount in the way).

  Each one can be switched off or set to "right away" / "when you move".
- **Waits until you're done.** After a fight the mount comes once you've been idle for one of your global cooldowns:
  a Pictomancer can paint motifs, a healer can top people up, you can press your buffs. (Or pick a fixed delay.)
- **Gets you off the mount when you use an action** the game won't let you use mounted, and then uses that action
  for you (can be turned off). Flying, you land first.
- **Gets you off for an enemy you target**: once it's within your attack range (the longest attack your job can always
  open with), or as soon as you target it. Flying, the landing starts early by how long it takes from your height,
  so you touch down right where you can attack. Melee jobs can choose to land at melee range instead.
- **Uses Sprint when it helps** (when it's faster than how you're moving right now) and **keeps Peloton up** on
  Archer, Bard, Machinist and Dancer, with Peloton taking over in Sprint's last seconds. Nothing when a mount is about
  to be summoned, or when you're about to fight.
- **Doesn't waste Sprint or Peloton.** One used right before you mount (mounting ends it) or right before your
  first attack only delays you: it's held back then, whoever used it.
- **Learns how you play.** Your running, Sprint, Jog and Peloton speeds, how fast you land and get off, and how long
  your actions take to go off are measured as you play, and everything is timed from them.
- **Tells you what's going on**, through the game's own message line: "hold still to land" (moving stops the game's
  landing) and "can't land here, move a little" (some spots can't be landed on). Both can be turned off.

## Works great with Beeline

[Beeline](https://github.com/KeydenK/Beeline) is a companion plugin by the same author: one button on the map takes you
anywhere, the quickest way (teleport, aethernet, mount, flying, walking). With both installed, Beeline decides when to
mount on its trips (it knows the route), Giddy Up handles Sprint, Peloton and getting off for a fight, and they share
what they've learned about your speeds. Each works fine alone.

To get it, add its repository the same way as Giddy Up's below, with this link:
```
https://raw.githubusercontent.com/KeydenK/Beeline/main/repo.json
```
then install **Beeline** from `/xlplugins`. Giddy Up's settings show "Beeline found" once it's there.

## Installation

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this link in an empty row, tick **Enabled**, then click the **+** button:
   ```
   https://raw.githubusercontent.com/KeydenK/GiddyUp/main/repo.json
   ```
3. Click **Save and Close**.
4. Type `/xlplugins`, search for **Giddy Up** and install it.

Settings: `/giddyup`. Everything is on by default; each option explains itself when you hover its (i).

## Settings

**On foot**

| Setting | What it does | Default |
| --- | --- | --- |
| Sprint when it helps | Out of combat, while you move, Sprint when it's ready and faster than how you move right now (Jog and Peloton included). Not when a mount is about to be summoned | On |
| Keep Peloton up | Archer, Bard, Machinist and Dancer (level 20+), out of combat, while moving; takes over in Sprint's last seconds, renewed just before it runs out | On |
| Moving for at least | How long you must have been moving before Sprint or Peloton is used, so a small step doesn't spend them | 0.4 s |

**Mounting**

| Setting | What it does | Default |
| --- | --- | --- |
| A zone change or teleport | Mount up after it | On, right away |
| Combat | Mount up after the fight | On, right away |
| Gathering or fishing | Mount up after it | On, when you move |
| Crafting | Mount up after it | On, when you move |
| Talking to someone or using something | Mount up after a conversation or after using a quest object, switch, chest... | On, when you move |
| Mount as soon as you're done | Wait until you've been idle for one global cooldown (not casting, no action used). Off: a fixed delay instead | On |
| Mount | Mount Roulette or any mount you've unlocked (searchable list) | Mount Roulette |

**Dismounting**

| Setting | What it does | Default |
| --- | --- | --- |
| Dismount when you use an action | Press an action you can't use mounted: you get off (landing first if flying). A press that wouldn't work anyway (out of range, on cooldown) leaves you on | On |
| Then use the action | Once you're off, Giddy Up uses the action you pressed (unless another action came first) | On |
| Dismount for an enemy you target | Once it's within your attack range, or as soon as you target it | On, within range |
| Melee jobs: land at melee range | Tanks and melee DPS stay mounted until they're within reach of their close-range attacks, instead of their ~20 y openers | Off |
| Remind me on screen | "Hold still to land" when an enemy is in range and you're still flying around | On |
| Tell me when the game won't land me | "Can't land here, move a little" when the game ignored the landing; Giddy Up tries again once you've moved | On |
| Riding: wait until you stop moving | On the ground, get off only once you hold still | Off |

**Advanced**

| Setting | What it does | Default |
| --- | --- | --- |
| Learned from how you play | Your measured speeds and timings, with a button to reset them | Learned automatically |
| Write troubleshooting details to the Dalamud log | Each thing Giddy Up does and why, as `[GiddyUp]` lines in `/xllog` | Off |

## Performance

Giddy Up reacts to events (a fight ends, you mount, you target something, you change zone) and only watches what
matters right now:
- **On foot, out of combat:** one quick check ten times a second (whether you're moving, which buffs you have).
- **Mounted:** one comparison per frame (has your target changed?); with an enemy targeted, a distance check, plus a
  height check four times a second while flying.
- **In combat, in cutscenes, between areas:** nothing.

Nothing is sent anywhere, and log lines never contain your character's name.

## Requirements

- [Dalamud](https://goatcorp.github.io/) (XIVLauncher)
- Nothing else. [Beeline](https://github.com/KeydenK/Beeline) is optional.

## License

MIT
