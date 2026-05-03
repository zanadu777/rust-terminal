# Copilot Instructions

## Project Guidelines
- User wants terminal startup directory to come from persisted app state (MainWindowVm serialization/deserialization) rather than hardcoded BaseDirectory in control.
- User prefers the embedded terminal to start silently without startup banner text or automatic Get-Location output.
- User expects terminal backspace to behave like normal character deletion.
- User wants Cargo status lines to appear yellow like RustRover.
- User wants the first typed command word to be yellow and terminal styling to reasonably match RustRover; only the first command word should be highlighted, not the whole typed command.
- User wants terminal to get focus when the working directory is set.
- User wants less coupling: MainWindowVm should listen to terminal command completion events and store the execution collection; do not modify the reusable PowershellTerminal control architecture without explicit confirmation.
- User prefers negligible code in MainWindow.xaml.cs and wants UI logic moved out of code-behind where practical.
- User does not want code pushed to GitHub unless the feature is actually working end-to-end.
- User does not want favorites management controls on the Favorites tab; favorites should be managed elsewhere (toolbar popup).
- When commands are running, buttons must not be double-pressable, and the active running button must remain full amber (not dulled).

## Code Style
- Do not prefix private fields with an underscore; avoid using leading '_' for private variables.
