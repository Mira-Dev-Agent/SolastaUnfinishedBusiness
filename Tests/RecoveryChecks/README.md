# Recovery backend checks

Run with .NET 9:

```powershell
dotnet run --project Tests/RecoveryChecks -- <absolute-project-local-fixture-directory>
```

The checks exercise bounded early-report retention, duplicates, out-of-window ticks, integer overflow, file replacement, restoration, repeated recovery, existing-archive refusal, locked files and recovery after an interrupted replacement. Fixture data stays in the supplied directory for inspection. No game, sockets or discovery executable is started.

The suite also exercises the coordinated speed-change barrier: exact-tick arrivals, stale IDs, duplicate acknowledgments, outsiders, changed rosters, six-player readiness, late-release refusal and 50 simulated slow-client schedules. The Photon adapter holds native network game time at the chosen tick and leaves Photon dispatch running, then applies the speed change when the current host releases the captured roster. A late prepare, timeout before release or roster change cancels the proposal. All peers need this protocol; older peers cannot acknowledge it, so a mixed-version proposal times out without changing speed. These tests verify the state machine; they do not replace live multiplayer testing of Unity scheduling and host migration.

`BoundedTickReports` and `ModInstallTransaction` are non-UI backend components. They do not activate a new multiplayer protocol or replace the existing updater by themselves. The transaction backend requires validated package contents, game exit, separate staging/target/backup directories and a caller that recovers incomplete journals before retrying installation. It retains backups instead of deleting failed archives. A successful unit run is not an in-game multiplayer test or evidence that a release has been installed.
