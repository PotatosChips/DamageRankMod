# Agent Context for FH Cards Mod

## External Game Source Path

- Slay the Spire 2 Core source is available at:
  - `C:\Users\Administrator\Desktop\Slay the Spire 2\src\Core`

## How To Use This Context

- When API behavior is unclear from this mod project, inspect the external Core source path above first.
- Prioritize checking these namespaces/classes when implementing card behavior:
  - `MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext`
  - `MegaCrit.Sts2.Core.Commands.CardSelectCmd`
  - `MegaCrit.Sts2.Core.Commands.CardPileCmd`

## Notes

- This repository references compiled libraries under `libs\` (for example `sts2.dll`), so not all API details are visible from local project files alone.
- Prefer validating assumptions against the external Core source before changing gameplay logic.
