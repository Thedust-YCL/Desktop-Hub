# DesktopHub.Core.Tests

This is a dependency-free .NET 8 console test suite. It exercises the production settings store and launch validation, rather than mocking success. Test JSON and inert invalid `.exe` files are created under a unique directory in the test build output and removed after the run.

```powershell
dotnet restore tests/DesktopHub.Core.Tests/DesktopHub.Core.Tests.csproj --configfile tests/DesktopHub.Core.Tests/NuGet.Config
dotnet run --project tests/DesktopHub.Core.Tests/DesktopHub.Core.Tests.csproj --no-restore
```

Successful process creation is an explicit integration option:

```powershell
dotnet run --project tests/DesktopHub.Core.Tests/DesktopHub.Core.Tests.csproj --no-restore -- --fixture <absolute-path-to-DesktopHub.LaunchFixture.exe>
```

Use only the project's inert `DesktopHub.LaunchFixture.exe` for that option. The default tests do not start any selected application or script. A returned PID proves Windows created a process; it does not prove that the program is fully initialized. The Windows host smoke test additionally covers the WebView2 bridge, picker, independent windows, and foreground restoration.

Security boundaries are enforced by the host method allowlist and picker provenance, and by the core GUID match, absolute local `.exe` validation, known interpreter/command-host denial, PE signature validation, and `UseShellExecute=false` with no arguments or elevation. The executable name checks are not a code-signing or malware policy: an executable chosen by the user can perform arbitrary behavior, and renamed or custom interpreters cannot be identified comprehensively by names. M1 never promises to sandbox the chosen program.
