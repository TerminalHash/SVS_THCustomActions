# Have3P (action 502) - reserved

Not merged in this iteration (see `Docs/Plugins/SVS_MAHelper_Doc.md`).

When implemented:

- Trigger: action `502`, ADV `a_502` (constants already reserved in
  `ActionVars.cs`).
- Logic goes in this folder as its own class (for example `Have3PFlow`).
- Register the trigger in `Plugin.RegisterTriggers()` and add the action to
  `ActionVars.Implemented` so the LoadFile watch can identify its ask.
- No changes to `Hooks.cs` should be needed.
