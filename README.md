# Fish Swarm Simulator

- CSD 3183
- By Group 9
- Cheng Ming Xian [c.mingxian@digipen.edu]
- Zhang Mingyang [mingyang.zhang@digipen.edu]
- Lee Jia Rui [l.jiarui@digipen.edu]
- Ng Juin Herng [juinherng.ng@digipen.edu]

---------------------------------------------------------------------------------------------------------------------------

## 1. Running the Simulation

Double click the Fish Swarming Simulator.exe executable to open the simulation.

---------------------------------------------------------------------------------------------------------------------------

## 2. The Benchmark / Profiler Window

The Benchmark window (titled *"Benchmark [C to toggle Config Menu]"*) is the profiler, used to monitor the simulation's performance metrics and shows you which optimisations are currently being used at a glance.

From top to bottom:

- **boid** — Current population of boids.
- **Burst** — Display for CPU Burst Setting.
  - `ON` = Burst-compiled parallel jobs.
  - `OFF` = plain single-threaded C#.
- **Mode** — Display for method used for neighbour-search/checks.
  - `Uniform Grid` = Uses dense uniform spatial grid.
  - `Naive O(n^2)` = no optimisations, every boid checks against every other boid up to perception radius.
- **Render** — Display for method used for rendering.
  - `Instanced` = CPU builds matrices, uses `RenderMeshInstanced`.
  - `GPU Indirect` = Transforms built in the vertex shader, uses `RenderMeshIndirect`.
- **Sim LOD** — Display for whether distant boids update less often.
  - `ON` = Boids further away update less often.
  - `OFF` = All boids update every frame.
  - *Note: Boids close to enemy will update every frame regardless of this setting.*
- **Backend** — Display for where and how work is done on the backend.
  - `CPU 1-thread` = single-threaded managed C#, Burst off.
  - `CPU Burst` = multi-core, Burst-compiled + SIMD.
  - `GPU Compute` = all per-boid work on the GPU.
  - *Note: `GPU Compute` forces use of hash grid, different from the spatial grid indicated by **Mode**.*

---

### Per-frame timing table

Three metrics, each shown as **now / avg / max** (milliseconds) — lower is better:

| Metric | Meaning |
|---|---|
| **Frame ms** | Total end-to-end frame time. `16.67 ms` = 60 fps, `33.34 ms` = 30 fps. |
| **Sim CPU ms** | CPU time spent in the simulation step. |
| **Steer ms** | Time spent on the neighbour-query / steering portion of the CPU sim. |

**FPS** — The number of frames the simulation runs per second. Shown as `now` and `avg`, derived from Frame ms.

- `avg` in both the table and FPS is calculated from the last 120 frames.

---

### The Rolling Graph

A rolling 120-frame history strip that shows:

- **Bars** — time taken for each frame, colour-coded:
  - `Green` < 16.67 ms (≥60 fps)
  - `Yellow` 16.67–33.34 ms (30–60 fps)
  - `Red` > 33.34 ms (<30 fps)
- **Cyan line** — shows **Sim CPU ms**.
- **White horizontal line** — shows the 60 fps mark (16.67 ms).

---------------------------------------------------------------------------------------------------------------------------

## 3. Keyboard Toggles 

These flip live on the *same* running population, so you can watch the cost change instantly.

| Key     | Toggles          | Effect                                                                                     |
|---------|------------------|--------------------------------------------------------------------------------------------|
| **C**   | Config window    | Show/hide the Config menu (Section 4 below).                                            |
| **B**   | Burst            | Switch between Burst-compiled parallel jobs ↔ single-threaded C#. *(CPU backend only.)*    |
| **Tab** | Neighbour search | Switch between **Spatial Grid** ↔ **Naive O(n²)**. *(CPU backend only.)*                   |
| **G**   | Render mode      | Switch between GPU **Instanced** ↔ **GPU Indirect**.                                       |
| **L**   | Simulation LOD   | Distant boid update less often ↔ every boid every frame.                                   |
| **J**   | Backend          | Switches between **CPU** ↔ **GPU Compute**.                                                |

*Note: `[G]` (indirect render) and `[J]` (GPU sim) only engage if the custom shaders compiled; otherwise the sim safely 
stays on the instanced/CPU path.

---------------------------------------------------------------------------------------------------------------------------

## 4. The Config Menu

A draggable window with two tabs: **boid** and **Predators**. Edits apply on the **next frame** (the manager re-reads the 
config every frame), and they work on a *runtime copy* so changes made are not permanent.

### 4.1 Boid
- **boid count** — five preset buttons: **100 / 1,000 / 10,000 / 100,000 / 1,000,000**. Clicking one **respawns** the whole school at that size (safe to do live).
- **Spawn** — `Spawn Radius`: how tightly the school is clustered when it spawns.
- **Simulation** — the same four toggles as the keyboard, as buttons: `Burst`, `Grid / Naive`, `LOD`, `Sim: GPU / CPU`.
- **Weights** — the core flocking balance (each 0–5, Threat 0–12):

| Slider          | What it does                                                             |
|-----------------|--------------------------------------------------------------------------|
| **Separation**  | How hard boid push apart to avoid crowding.                              |
| **Alignment**   | How strongly boid match neighbours' heading.                             |
| **Cohesion**    | How strongly boid pull toward the group centre.                          |
| **Boundary**    | How hard boid turn back from the tank walls.                             |
| **Threat**      | Strength of fleeing from predators (kept high so it wins over flocking). |

**Anti-clipping (separation)**      — fine control over close-range spacing so boid don't overlap:
                                      `Sep gain` (strength ramp)
                                      `Max sep force` (cap)
                                      `Sep radius` (personal-space distance).

**Movement / perception**:

| Slider              | What it does                                                                              |
|---------------------|-------------------------------------------------------------------------------------------|
| **Max speed**       | Top swimming speed.                                                                       |
| **Perception**      | Neighbour query radius (also the grid cell size). Bigger = each boid sees more neighbours.|
| **Max neighbours**  | Cap on neighbours considered per boid (4–64).                                             |

**School shape (cohesion axis)**    — `Axis X / Y / Z` (0.2–4): per-axis cohesion stiffness. 
                                      A higher value squashes the school on that axis, a lower value stretches it. 
                                      E.g. raise **Y** for a flatter, sheet-like school; lower **Z** to elongate it.

**Reset weights to start-of-sim**   — restores every slider to the values captured when Play began.


### 4.2 Predators 

Tunes **all predators in the scene at once**. If there are no predators you'll see a note instead.

| Slider                              | What it does                                                  |
|-------------------------------------|---------------------------------------------------------------|
| **Threat radius**                   | How close a predator gets before boid flee it.                |
| **Patrol cooldown**                 | Rest time between hunts while patrolling.                     |
| **Patrol / Chase / Recover noise**  | How wandery the predator's motion is in each of its states.   |
| **Noise freq**                      | How quickly that wander direction changes.                    |

---------------------------------------------------------------------------------------------------------------------------

## 5. Camera & Predator-view Controls

**Free-fly camera:**

| Control                       | Action                                  |
|-------------------------------|-----------------------------------------|
| **Right mouse (hold) + drag** | Look around (mouse-look).               |
| **W / A / S / D**             | Move forward / left / back / right.     |
| **Space / Shift**             | Move up / down.                         |
| **Ctrl (hold)**               | Speed boost while moving.               |
| **Mouse scroll**              | Zoom (moves the camera forward/back).   |


**Predator view:**

| Key         | Action                                                                        |
|-------------|-------------------------------------------------------------------------------|
| **F**       | Enter/toggle predator-follow view (ride along with a predator as it hunts).   |
| **A / D**   | Cycle to the previous / next predator (while in predator view).               |
| **Esc**     | Exit predator view (back to free-fly).                                        |

---------------------------------------------------------------------------------------------------------------------------

## 6. Notes

>    For the sake of your computer, please do not attempt to select 100,000 and above boids when on default settings, 
>    it is already very slow on 10,000. (**CPU Burst** OFF + `Naive O(n^2)` + `Instanced` + **Sim LOD** OFF + `CPU 1-thread`)

----------
