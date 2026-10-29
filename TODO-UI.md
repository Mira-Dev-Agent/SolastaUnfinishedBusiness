# Deferred UI work

Recorded 2026-10-29. This checklist documents work only. Implementation requires a new explicit, task-specific UI exception from the user. Earlier UI requests do not grant that exception. Existing uncommitted alert drafts are not approved for release.

## Multiplayer correction and recovery

- [ ] Show when a recovery autosave is being made before automatic correction. Describe a save as successful only after its completion callback confirms success.
- [ ] If saving fails or is unavailable, tell the host that automatic correction/reload was cancelled and recommend trying a manual save. Do not imply that a recovery save exists.
- [ ] Report correction failure, rejection, or verification timeout and recommend a host-led reload. Identify the affected player; distinguish an applied correction from a verified synchronized state.
- [ ] Give affected clients appropriate failure/recovery status as well as the host. Keep save/load actions host-controlled.
- [ ] Refer to the exact successful recovery save when available. If saving failed, do not offer an automatic load of an older quicksave as though it were the new recovery save.
- [ ] Avoid repeated alerts for the same correction ID. Handle separate failures for separate players without one player's success dismissing another's failure.

## Coordinated enemy-turn speed barrier

- [ ] Add clear feedback for a pending speed change and for gameplay actually waiting at the agreed tick. The networking barrier currently does not explicitly invoke the native “Waiting for player” notification.
- [ ] Identify which players have not arrived when a wait becomes noticeable. Explain that the previous speed remains selected until the host releases the change.
- [ ] Report a cancelled proposal, including a late proposal, pre-release timeout, or roster change. Avoid implying that the speed change succeeded.
- [ ] Cover automatic speed-up at the start of an enemy-turn run, not only manual toggles. Preserve the intentional absence of a routine player-triggered alert for automatic activation; surface meaningful waits/failures without repeated notifications.
- [ ] Preserve the existing pending button state and avoid overlapping or duplicating native network-wait messages. Keep network/service-failure feedback distinct from an ordinary brief coordination pause.

## Updater and installer integration

- [ ] Use the standard update-prompt actions **Update** and **Later**; provide a manual **Check for updates** control. Later must dismiss without downloading or installing.
- [ ] Display checking, download, integrity verification, staged/queued, installation failure and recovery outcomes accurately. Never report “up to date” after a failed check.
- [ ] Explain that a verified update is queued for installation after the game exits, followed by relaunch. Do not announce installation success merely because staging or helper startup succeeded.
- [ ] Explain rollback/recovery failures and where the preserved backup/log can be found. Do not offer a rollback target unless its backup is usable.
- [ ] Keep launch and gameplay responsive; prompt at a suitable menu boundary, without silently restarting or interrupting active play.

## Acceptance checks after authorization

- [ ] Localize new text using the existing translation system and check long translations, readability and controller navigation.
- [ ] Test host and client views with multiple affected players, slow loading, delayed packets, disconnects, host changes, save refusal/failure, cancellation and installer rollback failure.
- [ ] Verify alerts do not repeat every frame, steal focus unnecessarily, or announce success before backend completion.
- [ ] Verify that cancelling/dismissing UI does not silently release a synchronization barrier or bypass save/integrity checks.
- [ ] Record which changes were actually tested in-game. Backend tests and successful compilation alone do not establish UI behavior.

The installer backend and coordinated barrier exist in source, but the updater integration and recovery work have separate outstanding backend validation. This TODO is not evidence that those features are released. Hosting destination/upload approval is also separate from UI authorization.
