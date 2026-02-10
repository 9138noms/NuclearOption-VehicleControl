# Nuclear Option - Vehicle Control

Take direct WASD control of friendly ships and ground vehicles. Jump out of your aircraft, possess a nearby tank or corvette, and drive it yourself while AI handles turret targeting.

![BepInEx](https://img.shields.io/badge/BepInEx-5.x-blue) ![Game](https://img.shields.io/badge/Nuclear%20Option-Steam-black)

## Requirements

- [Nuclear Option](https://store.steampowered.com/app/2296550/Nuclear_Option/) on Steam
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (Unity Mono)

## Installation

1. Install BepInEx 5.x into your Nuclear Option game folder
2. Run the game once to generate the BepInEx folder structure
3. Copy `VehicleControl.dll` into `[Game Folder]\BepInEx\plugins\`
4. Launch the game

## Controls

| Key | Function |
|-----|----------|
| `F8` | Possess / unpossess nearest friendly unit (configurable) |
| `W` / `S` | Throttle forward / reverse |
| `A` / `D` | Steer left / right |
| `Space` | Brake (vehicles) / All stop (ships) |
| `C` | Toggle cruise control (vehicles only) |
| `Tab` | Cycle through enemy targets |
| `T` | Clear target (return to AI auto-targeting) |

## Features

### Vehicle Possession
- Press **F8** near any friendly ship or ground vehicle to take control
- Camera automatically follows the possessed unit
- Press **F8** again to release and return to your aircraft
- Only friendly units can be possessed — enemy vehicles are blocked

### Ground Vehicles (IFV, APC, SAM, etc.)
- Direct throttle/steering control via native memory writes to the physics job system
- AI movement is fully suppressed during possession
- Smooth input interpolation with decay on release

### Ships (Corvettes, Destroyers, etc.)
- Throttle holds position when released (like a real ship throttle lever)
- Space bar brings throttle to zero (all stop)
- Steering is automatically adjusted for ship conventions

### AI Turret Targeting
- AI weapon systems remain active while you drive
- Press **Tab** to manually designate targets for the AI turrets
- Press **T** to let AI pick its own targets
- Turret aiming and firing is handled automatically by the AI

### HUD Overlay
- Speed and heading display
- Current target info (name, distance)
- Unit name and health status
- Control hints

### Cruise Control (Vehicles)
- Press **C** to lock the current throttle — the vehicle maintains speed without holding W
- Press **C** again or **S** to disengage

### Configurable Keybind
- The possess key can be changed in `BepInEx/config/com.noms.vehiclecontrol.cfg`
- Default: F8

## Tips

- **Ground vehicles in Hold Position won't move.** Give them a movement order (waypoint) first, then possess them while they're moving.
- You can possess from spectator/free camera — you don't have to be flying.

## Notes

- **Singleplayer / Host only** — does not work as a multiplayer client
- AI steering is suppressed but weapon AI stays active, so your turrets will still engage enemies
- If your possessed unit is destroyed, control automatically returns to your aircraft
- WASD camera movement is disabled while possessing to prevent conflicts
