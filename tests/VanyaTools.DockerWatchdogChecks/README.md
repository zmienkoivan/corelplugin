# Docker watchdog diagnostics checks

The Windows .NET Framework 4.8 runner links the real `DockerUiWatchdog.cs`, uses separate STA threads with actual WPF dispatchers and captures log lines in memory. It never connects to Corel, shows a window, changes native input state, publishes an order or accesses a printer.

```powershell
dotnet run --project tests/VanyaTools.DockerWatchdogChecks/VanyaTools.DockerWatchdogChecks.csproj --configuration Release
```

Three scenarios verify that a blocked synchronous callback reports its nested operation and timing, that a Normal-priority probe can answer while Background work is starved, and that queued callbacks from previous Start/Stop generations do not run. Checks also cover instance/version/dispatcher identity, dispatcher-side disabled-input state, long-scope completion, recovery and nonduplicated Stop logs. Timing thresholds are shortened in the test constructor; production defaults remain a 2-second heartbeat and 15-second delay threshold.

This is diagnostic coverage, not a reproduction of a particular Corel hang. The watchdog never reenables controls, aborts threads, kills a process or retries an operation.
