#!/usr/bin/env python3
from pathlib import Path

program = Path("client/SCBL.Updater/Program.cs").read_text(encoding="utf-8")
restart = Path("client/SCBL.Updater/RestartCoordinator.cs").read_text(encoding="utf-8")
local = Path("client/ScblPublicLauncher/Services/LocalClientUpdateService.cs").read_text(encoding="utf-8")

assert 'x.Equals("--restart-helper", StringComparison.OrdinalIgnoreCase)' in program
assert "RestartCoordinator.RunHelper(target, restart, waitPidText)" in program
assert "RestartCoordinator.ScheduleAfterUpdaterExit(target, restart)" in program
assert "RestartCoordinator.LaunchImmediately(target, restart)" in program
assert "Environment.ProcessId.ToString()" in restart
assert 'Path.Combine(target, "SplinterCellCNLauncher.exe")' in restart
assert "WaitForExit(ParentExitTimeoutMs)" in restart
assert "LaunchAttempts = 20" in restart
assert "Thread.Sleep(LaunchRetryDelayMs)" in restart
assert "Updated launcher started successfully" in restart
assert 'string launcherExe = Path.Combine(baseDir, "SplinterCellCNLauncher.exe");' in local
assert "Environment.ProcessPath" in local
print("SCBL client update restart checks passed")
