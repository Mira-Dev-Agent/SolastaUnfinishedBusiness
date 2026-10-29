# Deferred UI work

Recorded 2026-10-29. Implemented on `develop` for 1.10.0 the same day, at the user's explicit request ("Perform the TODOs in the TODO-UI"), together with the backends the items needed (recovery saves before corrections, correction IDs and verification, the install-after-exit helper). A ticked item is built and covered by compilation and, where noted, by `Tests/RecoveryChecks`; **none of it is tested in game or in a multiplayer session yet**. The acceptance checks below stay open until it is.

## Multiplayer correction and recovery (`Models/MultiplayerDesyncContext.cs`)

- [x] Show when a recovery autosave is being made before automatic correction. Describe a save as successful only after its completion callback confirms success. *The save runs synchronously in one frame (`GameSerializationManager.SaveGame`), so the alert follows it: it names the save only after the game's `GameSaved` callback fired and the file exists.*
- [x] If saving fails or is unavailable, tell the host that automatic correction/reload was cancelled and recommend trying a manual save. Do not imply that a recovery save exists.
- [x] Report correction failure, rejection, or verification timeout and recommend a host-led reload. Identify the affected player; distinguish an applied correction from a verified synchronized state. *The client reports "applied"; only the next matching check is "verified"; 60 s without one is a timeout.*
- [x] Give affected clients appropriate failure/recovery status as well as the host. Keep save/load actions host-controlled.
- [x] Refer to the exact successful recovery save when available. If saving failed, do not offer an automatic load of an older quicksave as though it were the new recovery save. *Resync loads the exact file it just saved; before, a quick save that didn't finish in 30 s could lead to loading the previous quick save.*
- [x] Avoid repeated alerts for the same correction ID. Handle separate failures for separate players without one player's success dismissing another's failure.

## Coordinated enemy-turn speed barrier (`Models/MultiplayerTurnSpeedBarrier.cs`)

- [x] Add clear feedback for a pending speed change and for gameplay actually waiting at the agreed tick. *The button keeps its pending text; a hold over 1 s shows a line under it. The native "Waiting for player" notification is not invoked.*
- [x] Identify which players have not arrived when a wait becomes noticeable. Explain that the previous speed remains selected until the host releases the change. *The host sends the missing players every 2 s while holding.*
- [x] Report a cancelled proposal, including a late proposal, pre-release timeout, or roster change. Avoid implying that the speed change succeeded.
- [x] Cover automatic speed-up at the start of an enemy-turn run, not only manual toggles. Preserve the intentional absence of a routine player-triggered alert for automatic activation; surface meaningful waits/failures without repeated notifications. *After a cancelled automatic proposal the host stops proposing automatically until the next room.*
- [x] Preserve the existing pending button state and avoid overlapping or duplicating native network-wait messages. Keep network/service-failure feedback distinct from an ordinary brief coordination pause. *The line steps aside while `NetworkTick.WaitingForPlayer` is set; a host change or lost connection has its own reason.*

## Updater and installer integration (`Models/UpdateContext.cs`, `SolastaUnfinishedBusiness.Installer/`)

- [x] Use the standard update-prompt actions **Update** and **Later**; provide a manual **Check for updates** control. Later must dismiss without downloading or installing.
- [x] Display checking, download, integrity verification, staged/queued, installation failure and recovery outcomes accurately. Never report "up to date" after a failed check. *Integrity: GitHub's published size and SHA-256 of the release zip, then its contents.*
- [x] Explain that a verified update is queued for installation after the game exits, followed by relaunch. Do not announce installation success merely because staging or helper startup succeeded. *The helper writes a result file; the next start reports it and compares the running version.*
- [x] Explain rollback/recovery failures and where the preserved backup/log can be found. Do not offer a rollback target unless its backup is usable.
- [x] Keep launch and gameplay responsive; prompt at a suitable menu boundary, without silently restarting or interrupting active play. *Background thread; prompts wait for the main menu with no other message open. "Quit now" is the player's choice.*

## Acceptance checks after authorization

- [ ] Localize new text using the existing translation system and check long translations, readability and controller navigation. *Texts are in all 10 languages (machine translations, `scripts/add_recovery_update_terms.py`); not yet read in game.*
- [ ] Test host and client views with multiple affected players, slow loading, delayed packets, disconnects, host changes, save refusal/failure, cancellation and installer rollback failure.
- [ ] Verify alerts do not repeat every frame, steal focus unnecessarily, or announce success before backend completion.
- [ ] Verify that cancelling/dismissing UI does not silently release a synchronization barrier or bypass save/integrity checks.
- [x] Record which changes were actually tested in-game. Backend tests and successful compilation alone do not establish UI behavior. *Recorded in the 1.10.0 changelog entry: nothing of this is tested in game yet.*

The installer is only exercised against test folders (`Tests/RecoveryChecks` with the helper's path); a real update needs a newer release than the installed one. This TODO is not evidence that these features are released. Hosting destination/upload approval is also separate from UI authorization.
