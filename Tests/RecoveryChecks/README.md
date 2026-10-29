# Recovery backend checks

Run with .NET 9:

```powershell
dotnet run --project Tests/RecoveryChecks -- <absolute-project-local-fixture-directory>
```

The checks exercise bounded early-report retention, duplicates, out-of-window ticks, integer overflow, file replacement, restoration, repeated recovery, existing-archive refusal, locked files and recovery after an interrupted replacement. Fixture data stays in the supplied directory for inspection. No game, sockets or discovery executable is started.

`BoundedTickReports` and `ModInstallTransaction` are non-UI backend components. They do not activate a new multiplayer protocol or replace the existing updater by themselves. The transaction backend requires validated package contents, game exit, separate staging/target/backup directories and a caller that recovers incomplete journals before retrying installation. It retains backups instead of deleting failed archives. A successful unit run is not an in-game multiplayer test or evidence that a release has been installed.
