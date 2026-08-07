# Qualification inputs (gitignored)

Drop a **copy** of a real database here to convert the database-upgrade exit gate from an
inference into a measurement:

```
data/qualify/production-snapshot.db
```

Then:

```powershell
.\scripts\qualify.ps1 -DbSnapshot .\data\qualify\production-snapshot.db
```

The harness copies it again before touching it and runs the upgrade under an isolated
`ANTHILL_HOME`, so neither this snapshot nor your working installation is modified.

Nothing in this directory is committed. A production database contains real mission history,
credentials metadata, and operator records — it must never enter git.
