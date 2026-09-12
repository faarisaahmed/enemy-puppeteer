# Enemy Puppeteer

> **Experimental.** A test rig, not a gameplay mod.

Take manual control of any enemy in Hollow Knight: Silksong — pick it with the mouse, fire
its attacks from a menu, steer it with the arrow keys.

Built on the [Enemy Behavior API](https://github.com/faarisaahmed/enemy-behavior-api), and
built *for* it: the API's Influence and Override tiers had never executed in game, and that
class of bug is far easier to feel than to assert in a test. An Override holder that
swallows a death transition leaves an enemy standing in an unkillable loop — you notice
that in about four seconds of driving one by hand.

## What it does

- **Lists every enemy** the API has discovered in the current scene
- **Click an enemy in the world** to select it (or pick from the list)
- **Take control** — claims Override, which suppresses the enemy's own decision-making
- **Fire any discovered attack** from a button grid, labelled with shape and confidence
- **Steer with arrow keys**, using that enemy's own movement states
- **Live readout** of the enemy's current PlayMaker state and whether each command landed

## What it deliberately can't do

Arrow keys are **not a velocity puppet.** The Override tier fires state transitions — it
doesn't write position — so the honest ceiling is "make the enemy do something it already
knows how to do, now". Press left on an enemy with no leftward behaviour and nothing
happens. That's the correct outcome, and feeling that constraint is half the point of the
rig.

Horizontal is also generous on purpose: nothing in the schema records which *way* a
movement state sends an enemy — most of them go wherever it's already facing — so a walk is
offered for both left and right and the enemy's own facing logic decides.

## Use

1. Install the [Enemy Behavior API](https://github.com/faarisaahmed/enemy-behavior-api) first
2. Drop `EnemyPuppeteer.dll` in `BepInEx/plugins/EnemyPuppeteer/`
3. Press **F8** in game

Closing the window always releases control, so you can't strand an enemy under suppression
by forgetting.

### Policy toggle

- **SuppressDecisions** (default) — vetoes only transitions *out of a decision state*. The
  enemy still executes attacks, recoveries, hit reactions and death normally; it just stops
  choosing and waits for you. Most enemies still look like themselves.
- **SuppressAll** — vetoes everything you didn't fire. Total puppetry, and you now own
  reacting to being hit.

Either way a passlist (death, stun, recoil, land…) always gets through. **Whether that
passlist is correct is exactly what this rig exists to find out.** If an enemy under
control won't die, that's the bug — please report it.

## What to watch for

Things worth reporting, roughly in order of how much they'd matter:

| Symptom | What it means |
|---|---|
| Enemy won't die while controlled | The Override passlist is wrong. Worst case. |
| `FIRE FAILED` on an attack | No route into that state from where the FSM is |
| Attack fires but nothing visibly happens | Classification found a state that isn't really an attack |
| Enemy freezes and never recovers | Decision-state detection missed, or SuppressAll is too broad |
| An obvious attack is missing from the menu | Classifier missed it — the API's known weak spot |
| Arrow key does nothing | No movement state bound to that direction for this enemy |

The log panel at the bottom of the window shows the last few commands and whether they were
accepted, plus every state change and whether it was ours or the enemy's own.

## Building

Needs the .NET SDK, a local Silksong install with BepInEx, and the API DLL already in
`BepInEx/plugins/EnemyBehaviorApi/`.

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

It references `EnemyBehaviorApi.dll` as a plain assembly out of the plugin folder rather
than as a project reference — deliberately, so that a successful build is evidence the
public API is usable by an outside mod.

## Status

Everything it drives runs through API paths that have **never executed in game**. Expect
breakage; that's what it's for. Findings belong in
[the API's issues](https://github.com/faarisaahmed/enemy-behavior-api/issues).
