# FyteClub Plugin Modular Structure

The FyteClubPlugin.cs file has been broken down into logical modules for better maintainability and organization.

## Module Breakdown

### Core Files

1. **FyteClubPluginCore.cs** - Main plugin class
   - Initialization and dependency injection
   - Core Dalamud service management
   - Plugin lifecycle (constructor, dispose)
   - Public accessors for UI components

2. **FyteClubPlugin.SyncQueue.cs** - Sync Queue Management
   - Player detection event handling
   - Priority-based sync queue processing
   - Batch processing of mod synchronization
   - Hash change detection and re-queuing

3. **FyteClubPlugin.SyncshellManagement.cs** - Syncshell Operations
   - Syncshell creation and joining
   - P2P message handling setup
   - Received mod data processing
   - JSON property extraction helpers

4. **FyteClubPlugin.ModSharing.cs** - Mod Sharing & Synchronization
   - Manual mod sharing functionality
   - Automatic mod system change detection
   - Companion mod sharing
   - Mod data hash calculation

5. **FyteClubPlugin.Framework.cs** - Framework Updates & IPC
   - Framework update loop handling
   - IPC subscriber management
   - Periodic task scheduling (cache, reconnection, discovery)
   - Bulk cache application

6. **FyteClubPlugin.Configuration.cs** - Configuration Management
   - Configuration loading and saving
   - User blocking/unblocking
   - TURN server port management
   - Plugin recovery functionality

7. **FyteClubPlugin.CacheManagement.cs** - Cache Operations
   - Client and component cache initialization
   - Cache application and mod reconstruction
   - Player and companion change detection
   - Cache statistics and logging

8. **FyteClubPlugin.P2PConnections.cs** - P2P Connection Handling
   - P2P connection establishment
   - Known player connection attempts
   - Syncshell discovery for unknown players
   - Safe mod request processing

9. **FyteClubPlugin.Commands.cs** - Command Processing
   - Chat command handling (/fyteclub)
   - Debug functionality
   - Object type logging utilities

### UI Files

10. **UI/ConfigWindow.cs** - Main Configuration Window
    - Simplified UI that works with modular structure
    - Tab-based interface (Syncshells, Block List, Cache, TURN, Logging)
    - Event handling for UI interactions

## Benefits of Modular Structure

### Maintainability
- Each module has a single responsibility
- Easier to locate and fix bugs
- Reduced cognitive load when working on specific features

### Readability
- Related functionality is grouped together
- Clear separation of concerns
- Better code organization

### Testability
- Individual modules can be tested in isolation
- Easier to mock dependencies
- More focused unit tests

### Collaboration
- Multiple developers can work on different modules simultaneously
- Reduced merge conflicts
- Clear ownership of functionality

## Migration Notes

### From Original FyteClubPlugin.cs
The original file has been marked as deprecated with clear documentation about where each piece of functionality has moved.

### Partial Classes
All modules use `partial class` declarations, so they compile as a single class while maintaining logical separation in the codebase.

### Dependencies
- Core services are initialized in FyteClubPluginCore.cs
- Each module accesses shared services through the main class
- No circular dependencies between modules

### State Management
- Shared state remains in the core class
- Each module can access and modify shared collections safely
- Thread-safe collections are used where appropriate

## Usage Guidelines

### Adding New Features
1. Determine which module the feature belongs to
2. If it doesn't fit existing modules, consider creating a new one
3. Keep modules focused on their primary responsibility

### Modifying Existing Features
1. Locate the appropriate module
2. Make changes within that module's scope
3. Update related modules if necessary

### Debugging
1. Use the modular logging system (LogModule enum)
2. Enable specific module logging for targeted debugging
3. Each module logs with its appropriate context

This modular structure maintains all existing functionality while making the codebase much more manageable and easier to understand.