# Copilot Instructions

## Project Guidelines
- User wants terminal startup directory to come from persisted app state (MainWindowVm serialization/deserialization) rather than hardcoded BaseDirectory in control.
- User prefers the embedded terminal to start silently without startup banner text or automatic Get-Location output.
- User expects terminal backspace to behave like normal character deletion.
- User wants Cargo status lines to appear yellow like RustRover.
- User wants terminal styling to reasonably match RustRover and does not want the whole typed command colored; only the first command word should be highlighted.
- User wants less coupling: MainWindowVm should listen to terminal command completion events and store the execution collection; do not modify the reusable PowershellTerminal control architecture without explicit confirmation.
- User prefers negligible code in MainWindow.xaml.cs and wants UI logic moved out of code-behind where practical.

## Code Style
- Do not prefix private fields with an underscore; avoid using leading '_' for private variables.
