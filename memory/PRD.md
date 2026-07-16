# JARVIS Monitoring — PRD

## Problem statement (verbatim)
> JARVIS is a monitoring tool. Improvise animation across the app:
> 1. Loading animation and every animation should be emphasised
> 2. Globe rotating in the centre should have animation
> 3. Page/click transitions should have an opening animation so the UI feels awesome and aesthetic

User confirmed: "go all out" — full-cinematic Iron-Man-style HUD upgrade.

## Stack
- Angular 17 (standalone components) — `Monitoring_UI/`
- Three.js 0.185 + GSAP 3.15
- .NET 8 backend (`Monitoring_API/`) + Postgres — untouched in this iteration
- Docker Compose for local run

## Users / roles
- **Admin** (`devops`/`Cargo-Watch-2026!` local): full nav (Dashboard, Pipelines, History, Targets, Users, Activity, Settings)
- **Developer**: read-only Dashboard / Pipelines / History

## What's been implemented (2026-01-16 — animation pass v2)

### v2 additions (this iteration)
- **Readability fix**: Removed the intrusive `body::after` vertical scan-bar that was crossing text on the dashboard. `body::before` raster grain dropped to opacity 0.35 with lighter red so it never obscures readable content.
- **New font system** (loaded from Google Fonts in `index.html`):
  - `--font-display`: **Orbitron** — blocky sci-fi wordmark (used for "JARVIS" logo, boot wordmark, page headings)
  - `--font-hud`: **Chakra Petch** — geometric HUD sans (used for tracked-out UI labels)
  - `--font` (body): **Rajdhani** — condensed reading sans
  - `--mono`: **Share Tech Mono** — HUD data monospace (log lines, codes, uplink tags)
- **JARVIS reticle cursor** (`shared/custom-cursor/*`) — completely rebuilt:
  - Rotating outer ring with 8 tick segments + dashed inner ring
  - 4-directional crosshair blades that expand outward on hover
  - Corner brackets that fade in on hovering interactive elements
  - Pulsing radial halo around the center dot
  - White-flash pressed state
  - Auto-hides on touch/coarse-pointer devices
- **Cinematic login → dashboard globe transition** (`services/login-intro.service.ts` — body-mounted imperative overlay, NOT Angular ngIf):
  - Login page now shows a live preview HUD globe next to the sign-in card, with its own reticle rings + tick marks + `JARVIS · ORBIT · SYNC` label
  - Successful login → verify (HTTP) → auth.commit (flips signal to authenticated) fires simultaneously with intro.play() which appends a body-level `<div class="jv-intro-overlay">`
  - Overlay is fully bypassed from Angular's change detection so it can't be prematurely unmounted by unrelated CD cycles (fixes issue where signal/observable-driven `*ngIf` was unmounting the intro at ~800ms mid-animation when dashboard's status polling fired CD)
  - GSAP timeline plays: overlay fade-in → globe materialises small + rotated → concentric rings + tick marks reveal → radial energy burst peaks → **ACCESS · GRANTED** label tracks-out with `// JARVIS UPLINK ESTABLISHED` subtitle → globe contracts + drifts upward → overlay fades revealing dashboard
  - Globe rendered via layered CSS 3D wireframe (not a second WebGL context, so it's instant/cheap)
- **AuthService split into `verify()` + `commit()`** so login screen can validate credentials WITHOUT flipping the auth signal (which used to destroy LoginComponent mid-flight)
- **Login page HUD frame** — 4 corner brackets + top/bottom system tags around the whole login viewport
- Updated header `h1` to Orbitron with tracked-out uppercase treatment
- Bumped production initial bundle budget to 1.5MB to accommodate the intro overlay + fonts
1. **Boot splash → cinematic** (`shared/jarvis-boot/*`):
   - Multi-ring choreography (5 concentric rings, dashed + solid + dotted)
   - Arc-reactor pulsing core with radial glow halo
   - Radar sweep (conic-gradient) rotating continuously
   - SVG HUD arcs with tick marks + gradient stroke
   - Letter-by-letter wordmark reveal with flicker glitch + cyan chromatic aberration
   - Left/right data-stream columns with staggered fade
   - `[OK]/[..]` typewriter boot log (4 lines, ~2.5s)
   - Loading progress bar (shimmer + fill)
   - HUD frame with 4 corner brackets + top/bottom system tags

2. **Globe upgrade** (`shared/hud-globe/hud-globe.component.ts`):
   - **Data-arcs**: continuous animated curved arcs bouncing between random beacon points on the sphere with traveling head sprites
   - **Pulsing beacons**: 6 fixed beacons each with a shockwave ring that expands + fades on its own phase
   - **Orbiting satellites**: 3 sats on tilted orbits with visible red/cyan orbit trails
   - **Outer HUD reticle**: 48-segment tick-marked ring counter-rotating outside the globe
   - Status-driven speed/color/opacity (up=steady red, degraded=faster white, down=fast red)

3. **Page transitions** (`services/view-transition.service.ts` + `motion.css`):
   - Full-screen iris/HUD wipe overlay on every route swap (red scanline sweep + corner brackets + `// SWITCHING PANEL...` label)
   - New view fades + slides + slightly tilts in with 3D perspective
   - Direct children of new view stagger in

4. **Interaction upgrades**:
   - Global click ripple (HUD-red radial ripple originating at pointer) on every button/link/tab (`app.component.ts` global listener)
   - Magnetic directive now includes 3D rotateX/Y tilt + magnetic X/Y pull
   - Header buttons: animated gradient border + lift on hover
   - Nav tabs: sliding vertical accent bar + indent on active/hover
   - Logo icon: breathing glow + two concentric rotating rings

5. **Ambient motion** (`styles.css`):
   - Body-level scanline sweeping vertically every 9s (mix-blend: screen)
   - Faint horizontal HUD raster overlay (2% opacity, 4px pitch)
   - Enhanced button transitions (transform+scale on press, brightness lift on hover)

6. **Motion system** (`motion.css`):
   - New tokens: `--ease-hud`, longer `--dur-entrance` (520ms), `--dur-base` (260ms)
   - New keyframes: `jv-glow-breath`, `jv-scanline`, `jv-ripple-anim`
   - Iris overlay CSS with 4-corner brackets + scan gradient + label choreography
   - Skeleton loader now double-shimmers (base + red overlay)
   - All animations respect `prefers-reduced-motion`

## Verified
- `ng build` (both dev and production) — succeeds, no TS errors
- Live screenshot verification:
  - Boot splash renders with all HUD elements
  - Dashboard globe shows spinning wireframe + data arcs + satellites + beacons + reticle + corner brackets + scanline sweep
  - Transition iris overlay fires between routes with corner brackets + label

## Backlog / next
- P1: Sound design (subtle HUD chime on route swap, arc-reactor hum)
- P2: WebGL fallback: static SVG globe if Three.js fails
- P2: Per-status globe color palette (green for all-up, amber for degraded)
- P3: Micro-interactions on env-tab click (radar ping outward from tab)

## Files touched
- `Monitoring_UI/src/motion.css`
- `Monitoring_UI/src/styles.css`
- `Monitoring_UI/src/app/app.component.{ts,css}`
- `Monitoring_UI/src/app/shared/jarvis-boot/*` (html, ts, css)
- `Monitoring_UI/src/app/shared/hud-globe/hud-globe.component.ts`
- `Monitoring_UI/src/app/shared/magnetic.directive.ts`
- `Monitoring_UI/src/app/services/view-transition.service.ts`
- `Monitoring_UI/src/app/dashboard/dashboard.component.{html,css}`
