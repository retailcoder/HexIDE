### Added

- **Code lenses are drawn in the code window.** What a language server offers against a line, such as *Run test* or
  *3 references*, is shown after the line it starts on, dimmed, and runs its command when clicked. A lens whose
  command no running server declares is shown as text. A server that sends `workspace/codeLens/refresh` has its
  lenses asked for again.
