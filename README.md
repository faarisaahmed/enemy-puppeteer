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
- **Click an enemy's hitbox** to select it (or pick from the list)
- **Take control** — claims Override and enters *puppet mode*: Hornet becomes
  invulnerable, the camera locks onto the enemy, and its body is yours to steer
- **Fire any discovered attack** from a button grid, labelled with shape and confidence
- **Steer with arrow keys**, using that enemy's own movement states
- **Live readout** of the enemy's current PlayMaker state and whether each command landed
- **Rescan** button for enemies that were already awake before the API loaded

## Firing attacks the game never shows you

Most enemies have states you will never see in normal play. The mod reads the FSM graph,
not the play history, so every state Team Cherry authored is listed and firable.

In one recorded session, 20 attacks never fired during play - and every one of them was
still *reachable*, none orphaned. So this is mostly conditional gating rather than deleted
content: caged variants whose `UNCAGED` event only a cage broadcasts, arena-specific
setups, guards on distance or phase that rarely hold.

`Fire()` bypasses the guard - it sends the trigger event directly, or falls back to
`SetState` and jumps straight in. That is why the rig can show you behaviour the game
never does, and also why an attack fired out of context sometimes looks broken: it was
authored to run only after something else set it up.

## Puppet mode

Taking control does four things:

- **Hornet is invulnerable** — via the game's own `AddInvulnerabilitySource`, so it composes
  with anything else granting invulnerability instead of stomping a shared flag
- **The camera follows the enemy**, as though it were the player character
- **Arrow keys steer it directly** — left/right always, up/down for flyers
- **The FSM is fully suppressed** (`SuppressAll`), so the enemy stops fighting your input

Attacks still go through the API's `Fire()`. Movement does not, and that split is
deliberate: the Override tier fires *state transitions*, and steering is a velocity held
over time rather than a state an enemy can be told to enter. So movement writes the
rigidbody directly while attacks stay on the public API.

Everything puppet mode changes — invulnerability, hero layer, camera control, gravity — is
recorded on entry and restored on exit. Closing the window releases it.

### Being unnoticed is best-effort

Hornet's layer is moved to *Ignore Raycast* while puppeteering, which defeats enemies that
find her by raycast or physics overlap. It will **not** stop an enemy that looks her up
through `HeroController` directly, and plenty do. So some enemies will still track and
attack her — she just can't be hurt. Fully hiding the player is not something this can
promise generically.

### Enemies with no rigidbody

Fall back to firing the enemy's own movement states instead of direct steering — the older,
weaker behaviour. Press a direction with no matching state and nothing happens, which is
correct.

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
