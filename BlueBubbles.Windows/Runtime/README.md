# Vendored Windows App SDK runtime files

## `Microsoft.WindowsAppRuntime.Insights.Resource.dll`

A localized resource DLL for the Windows App SDK's Insights (telemetry) component.

**Why it's here.** Our app ships *unpackaged and self-contained* (`WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`). That publish layout bundles the core WindowsAppRuntime but
**does not** lay down this Insights resource DLL. `AppNotificationManager.Default.Register()`
nonetheless tries to load it during startup and throws
`"The specified module could not be found. ... Microsoft.WindowsAppRuntime.Insights.Resource.dll"`,
which leaves toasts visible but their click/reply/react **activations unable to route back**.

A dev build doesn't hit this because it binds against the machine-wide WindowsAppRuntime
*framework* package (which contains the DLL). An installed machine has no such framework, so
the file must travel with us. The `.csproj` copies it next to the published `.exe`, and the
Inno installer copies the publish folder recursively.

**Provenance.** Extracted from the matching framework package on a dev machine:

```
C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.2_2.0.1.0_x64__8wekyb3d8bbwe\
    Microsoft.WindowsAppRuntime.Insights.Resource.dll
```

FileVersion `2.0.1.0` — matches `<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.0.1" />`.

**On an SDK bump:** refresh this DLL from the new `Microsoft.WindowsAppRuntime.<ver>` framework
package so its version stays in lockstep with the referenced SDK. Find the source with:

```powershell
Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.*" |
  Select-Object Name, Version, InstallLocation
```
