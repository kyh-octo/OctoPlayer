# Signed release build

Use an existing Microsoft Artifact Signing account and externally managed credentials. Do not commit metadata, tokens, certificate keys, or build outputs.

Run with PowerShell 7 (or an appropriately configured Windows PowerShell host):

```powershell
$env:OCTO_CODESIGN = '1'
$env:OCTO_SIGN_PROVIDER = 'ArtifactSigning'
$env:OCTO_SIGNTOOL = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$env:OCTO_SIGN_DLIB = '<absolute path to Azure.CodeSigning.Dlib.dll>'
$env:OCTO_SIGN_METADATA = '<absolute path to external signing metadata JSON>'
# Set AZURE_CONFIG_DIR to the existing Azure CLI authentication directory.
# Prepend the existing Azure CLI bin directory to this process PATH if required.
./installer/build-installer.ps1 -SkipWebsite -RequireSignedRelease
```

The build signs only our OctoPlayer.exe before packaging, and Inno Setup signs the installer and embedded uninstaller. Vendor assemblies, runtime files and native LibVLC plugins are not re-signed. SHA-256 file/digest algorithms and the Microsoft RFC3161 timestamp service are used. SignTool verification and Windows Authenticode Valid plus a timestamp certificate are required; signing errors abort the build. SHA256SUMS.txt is emitted alongside the installer.

`installer/release.ps1 -SkipWebsite -RequireSignedRelease` forwards the signed-release requirement and uploads the checksum. Signed builds suppress the legacy automatic website-checkout mutation. Website release publication is coordinated separately, using product-prefixed tags and --latest=false; this does not edit the website main branch.

A valid publisher signature does not guarantee that Windows SmartScreen will suppress reputation warnings.

Do not run an installation/removal test against a real user's registration/settings without backup and restoration. The existing uninstaller kills OctoPlayer.exe and removes its AppData settings; that pre-existing removal behavior is unchanged in this signing patch. Isolated verification should use a separate directory and snapshot the affected per-user registrations and settings. Document GUI playback, upgrade and OS coverage limits honestly.
