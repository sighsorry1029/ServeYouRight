| `Version` | `Update Notes`    |
|-----------|-------------------|
| 1.0.5     | - Batch newly discovered mod settings into one save after food injection, reducing unnecessary config writes and reload events.<br>- Remove redundant WackyDB source caching and reduce allocations when resolving category labels.<br>- Group configuration data with its owner and document Debug builds with optional local game deployment. |
| 1.0.4     | - Fixed an infinite configuration reload loop caused by the file watcher detecting the plugin's own config save |
| 1.0.3     | - Improved modded food injection stability, live config reload handling, WackyDB clone detection, and release packaging |
| 1.0.2     | - ServingTray now removes item particles from some modded foods when they are placed |
| 1.0.1     | - Fixed modded feasts not being abled to place with the ServingTray |
| 1.0.0     | - Initial Release |
