# Changelog

## 0.7.3
- Checked against the current game (R.E.P.O. v0.5.3, Unity 6).
- The F6 panel now holds the game's controls with the game's own input switches, the way its menus do: no walking, looking, grabbing or pause menu while the panel is open. Push-to-talk keeps working. 0.7.2 patched Unity's input methods for this, which only stopped mouse look and mouse buttons; key presses still reached the game. Those patches are gone, and the plugin no longer patches anything.
- Esc closes the panel.
- While you are dead, the trail starts from the player you are spectating.
- When no full route exists, the trail shows the closest one and says so once. A failed path search is retried every 0.75 s instead of every frame.
- The target is moved onto the walkable area (up to 4 m), so an extraction point that sits slightly off the NavMesh still gets a trail.
- The cached route is dropped when the level changes, including level changes that do not load a new scene.
- Lowering `MaxDots` hides the extra dots straight away.
- If a game update removes something the plugin reads, the log names what is missing and F4 says the mod needs an update. An error while drawing the trail is logged once; after 5 in a row the trail turns itself off until you press F4.
- The config file is written once at startup and once when the panel closes, not on every slider step.
- `SpacingMeters` (0.5 to 4), `MaxDots` (1 to 100), `DotScale` (0.02 to 0.5), `FlowSpeedMetersPerSec` (0 to 6), `FadeZoneFraction` (0.05 to 0.5) and `PositionSmoothRate` (0 to 60) now have ranges; a value outside its range is clamped.
- The DLL now reports the same version as the package.
- Added `CHANGELOG.md` to the package so the Thunderstore Changelog tab is populated.

## 0.7.2
- Ported the InputBlocker pattern from REPOBlacklist so dragging customize-panel sliders no longer rotates the camera or fires weapons. Harmony prefixes on legacy `Input.*` axis / mouse-button reads and on `InputAction.ReadValue<Vector2/float/Vector3>()` (bound via reflection); `GetKey*` deliberately untouched so hotkeys still work.

## 0.7.1
- Fixed long-path "conveyor belt" rubber-banding: when `activeCount == MaxDots`, the wrapping dot's slot transitioned `MaxDots-1 → 0` while staying inside the visible branch, so the smoothing lerp animated it across the entire trail length. The frontPool decrement now also clears `_wasVisible` for the pool index that just wrapped, forcing a snap.

## 0.7.0
- Added the F6 customization UI: RGBA sliders, hex input, eight color presets, dot-size / flow-speed / spacing sliders, Reset Visuals, all live-bound to the BepInEx ConfigEntries.
- `PathfinderManager` subscribes to `DotColorHex.SettingChanged` and refreshes its cached `_baseColor` immediately, so dragging color sliders updates world dots on the same frame.

## 0.6.0
- Added F5 destination toggle: **Auto** (game-driven extraction → truck) and **Truck** (force truck regardless of extraction state). Mode is persisted as a `PathfinderTargetMode` ConfigEntry. Cache invalidated on toggle so the trail re-orients on the next frame.

## 0.5.0
- Replaced per-frame recompute with a cached path + per-frame player projection. Path is recomputed only on target identity change or when the player drifts more than `OffPathRecomputeThresholdMeters` from the cached polyline. Walking forward / backward along the path no longer translates dot world positions at all — the dots are nailed to absolute world coordinates; only their visibility shifts as the player walks past them.
- `RecalcIntervalSeconds` is now deprecated and ignored.

## 0.4.0
- Added per-dot exponential-decay smoothing on world positions (`PositionSmoothRate`, default 25). `_wasVisible` flags reset on every path recompute and on hidden→visible transitions so wraps still snap rather than draw a curved interpolation across geometry.

## 0.3.0
- Replaced per-dot independent `d %= trailExtent` wrap with a shared `_animOffset` plus circular pool pointer `_frontPool`. Spacing now stays perfectly even regardless of how `availableLength` changes; previously a moving modulo divisor could shake dots off the spacing grid.
- Added config-version migration: stale `DotColorHex` / `DotScale` defaults from 0.1 / 0.2 are overwritten with the new defaults; user-customized values are preserved.

## 0.2.0
- Added continuous flow animation: `_animOffset` advances every frame and the trail recycles continuously instead of populating-then-disappearing in waves.
- Switched per-dot color updates to a single shared material plus `MaterialPropertyBlock` for alpha fade.
- Smaller / more translucent default dot visuals.

## 0.1.0
- Initial implementation: F4 toggle, NavMesh path computation from `PlayerAvatar.LastNavmeshPosition` to `RoundDirector.extractionPointCurrent` (or `LevelGenerator.LevelPathTruck` when all extractions are complete), primitive sphere pool with `Sprites/Default` material at render queue 3500.
