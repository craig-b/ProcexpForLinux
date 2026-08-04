// Privileged helper daemon. Far smaller than the macOS XPC helper: on Linux
// most of /proc is world-readable, so this exists only for the handful of
// per-process files the kernel restricts to the owning uid — io, smaps_rollup
// and environ — plus privileged control actions.

Console.WriteLine("procexp-helper: scaffold only, service not yet implemented.");
return 0;
