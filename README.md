# Enemy Puppeteer

> **Experimental.** A test rig, not a gameplay mod.

Take manual control of any enemy in Hollow Knight: Silksong — click it, walk it around, and
fire its attacks yourself.

Built on the [Enemy Behavior API](https://github.com/faarisaahmed/enemy-behavior-api), and
built *for* it: the API's Influence and Override tiers had never executed in game, and that
class of bug is far easier to feel than to assert in a test. An Override holder that
swallows a death transition leaves an enemy standing in an unkillable loop — you notice that
in about four seconds of driving one by hand.

## What it does

- **Lists every enemy** the API has discovered in the current scene
- **Click an enemy's hitbox** to select it (or pick from the list)
- **Take control** — claims Override and enters puppet mode
- **Fire any discovered attack** from a button grid, labelled with shape and confidence
- **Live readout** of the enemy's current PlayMaker state and whether each command landed
- **Rescan** button for enemies that were already awake before the API loaded

## Puppet mode

Taking control makes Hornet an **invisible marker** and hands you the enemy's body.

- **Left-click** to move the marker — a crosshair shows where it is, and the enemy walks
  there using its own patrol walk
- **Right-click** to select a different enemy
- **Attack buttons** fire when *you* decide, aimed at wherever the marker is standing
- **Movement buttons** cover jumps, dashes and teleports — for ledges, gaps and closing fast
- Hornet is **invulnerable**, **invisible**, and **cannot act** — no needle, no movement

### The AI is deliberately not driving

An earlier version let the enemy hunt the marker on its own. It looked convincing, but it
meant the *AI* was playing: it chose the attack, the timing and the spacing, and the person
driving was reduced to placing bait. For a fight between two people that is not enough —
whoever is driving has to choose when to commit.

So decisions are suppressed (`SuppressDecisions`) and the enemy is walked manually.
Practically this also makes it unaware of Hornet: it can notice her all it likes, it simply
cannot act on it until you fire something.

### Why the patrol walk

Locomotion reuses the unhurried back-and-forth an enemy does *before* it has noticed
anything. That state is the right primitive for three reasons: nearly every enemy has one,
it isn't tied to a target, and it carries the creature's normal gait and speed. Chase and
dash states are avoided deliberately — they read as committed lunges, and a chase would drag
the enemy at the marker on its own terms, which is exactly what's being taken away from it.

Only **facing** and *is the walk running* are driven. Speed, animation and ground handling
stay with the enemy's own state, which is the only way the movement reads as that creature
walking rather than an object being dragged. Team Cherry's movement actions are largely
scale-relative, so facing is the steering wheel. Which sign of X scale means "right" is
per-enemy art, so there's a **Facing** toggle if something moonwalks.

### Hornet is hidden, not removed

Her renderers are disabled and her input is blocked through the game's own
`AddInputBlocker`. She stays physically present on purpose: enemy attacks aim at her, so the
marker is what makes a fired attack land somewhere meaningful.

She's kinematic and re-pinned every frame, since knockback, conveyors or an enemy walking
into her would otherwise drag the marker around mid-fight.

An earlier version moved her to the *Ignore Raycast* layer to stop enemies seeing her. That
also removed terrain collision and dropped her out of the world.

### Enemies that cannot walk

Some are stationary by design and have no walk state at all. The UI says so rather than
leaving you clicking at an enemy that will never move.

## Firing attacks the game never shows you

Most enemies have states you'll never see in normal play. The mod reads the FSM graph, not
the play history, so every state Team Cherry authored is listed and firable.

In one recorded session, 20 attacks never fired during play — and every one was still
*reachable*, none orphaned. So this is mostly conditional gating rather than deleted
content: caged variants whose `UNCAGED` event only a cage broadcasts, arena-specific setups,
guards on distance or phase that rarely hold.

`Fire()` bypasses the guard — it sends the trigger event directly, or falls back to
`SetState` and jumps straight in. That's why the rig can show you behaviour the game never
does, and also why an attack fired out of context sometimes looks broken: it was authored to
run only after something else set it up.

## Use

1. Install the [Enemy Behavior API](https://github.com/faarisaahmed/enemy-behavior-api) first
2. Drop `EnemyPuppeteer.dll` in `BepInEx/plugins/EnemyPuppeteer/`
3. Press **F8** in game

Closing the window always releases control, so you can't strand an enemy under suppression
by forgetting.

### Policy toggle

- **SuppressDecisions** (default) — vetoes only transitions *out of a decision state*. The
  enemy still executes attacks, recoveries, hit reactions and death normally; it just stops
  choosing.
- **SuppressAll** — vetoes everything you didn't fire. Total puppetry, and you now own
  reacting to being hit.

Either way a passlist (death, stun, recoil, land…) always gets through. **Whether that
passlist is correct is exactly what this rig exists to find out.** If an enemy under control
won't die, that's the bug — please report it.

## What to watch for

| Symptom | What it means |
|---|---|
| Enemy won't die while controlled | The Override passlist is wrong. Worst case. |
| `FIRE FAILED` on an attack | No route into that state from where the FSM is |
| Attack fires but nothing visibly happens | Classification found a state that isn't really an attack |
| Enemy freezes and never recovers | Decision-state detection missed, or SuppressAll is too broad |
| An obvious attack is missing from the menu | Classifier missed it — the API's known weak spot |
| Enemy moonwalks | Facing sign is inverted for that enemy — use the toggle |

The log panel shows the last few commands and whether they were accepted, plus every state
change and whether it was ours or the enemy's own.

## Building

Needs the .NET SDK, a local Silksong install with BepInEx, and the API DLL installed.

```bash
dotnet build EnemyPuppeteer/EnemyPuppeteer.csproj
```

Point it at your game with a gitignored `LocalPaths.props` beside `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <GamePath>C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong</GamePath>
  </PropertyGroup>
</Project>
```

The API DLL is found under `plugins/` automatically; set `<EnemyBehaviorApiPath>` in the
same file if you keep it somewhere unusual. It's referenced as a plain assembly rather than
a project reference — deliberately, so a successful build is evidence the public API is
usable by an outside mod.

## Status

Everything it drives runs through API paths that have seen very little use. Expect breakage;
that's what it's for. Findings belong in
[the API's issues](https://github.com/faarisaahmed/enemy-behavior-api/issues).
