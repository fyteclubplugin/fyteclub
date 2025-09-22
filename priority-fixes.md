# Mod Priority System Fixes

## Issues Fixed

### 1. Penumbra Priority Handling
- **Problem**: Plugin was applying all mods with default priority, potentially overriding user's enabled mods
- **Fix**: Use priority 0 (lowest) and forceAssignment: false to respect user's mod priorities
- **Result**: FyteClub mods won't override user's personal mod setup

### 2. Glamourer Lock System
- **Problem**: Plugin wasn't properly using Glamourer's lock system for priority handling
- **Fix**: Enhanced lock code usage with proper logging
- **Result**: Glamourer applications respect existing customizations

### 3. Collection Assignment Strategy
- **Problem**: Force-assigning collections could override user's collection setup
- **Fix**: Use non-forced assignment to respect existing collection assignments
- **Result**: User's collection preferences are preserved

## How Priority Works Now

### Penumbra
```
User's Mods (Higher Priority) → Override → FyteClub Mods (Priority 0)
```

### Glamourer
```
User's Customizations → Lock System → FyteClub Appearance (Lock: 0x46797465)
```

### SimpleHeels & Honorific
```
User's Settings → IPC Availability Check → FyteClub Values (if no conflicts)
```

## Technical Details

- **Penumbra**: Uses `priority: 0` and `forceAssignment: false`
- **Glamourer**: Uses unique lock code `0x46797465` ("Fyte" in ASCII)
- **Other Plugins**: Check IPC availability before applying to avoid conflicts

This ensures FyteClub mods enhance appearance without breaking user's personal setup.