# Downloads the offline keyword spotter (Vosk) into the Unity project.
# Run once from the repository root:  powershell -ExecutionPolicy Bypass -File unity\setup_vosk.ps1
$ErrorActionPreference = "Stop"
$assets = Join-Path $PSScriptRoot "WhatIf\Assets"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("vosk_" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null

Write-Host "Downloading Vosk (C# wrapper + Windows libraries, Android library, small English + German models)..."
Invoke-WebRequest "https://www.nuget.org/api/v2/package/Vosk/0.3.38" -OutFile "$tmp\vosk.zip"
Invoke-WebRequest "https://repo1.maven.org/maven2/com/alphacephei/vosk-android/0.3.47/vosk-android-0.3.47.aar" -OutFile "$tmp\vosk-android.zip"
Invoke-WebRequest "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip" -OutFile "$tmp\model.zip"
Invoke-WebRequest "https://alphacephei.com/vosk/models/vosk-model-small-de-0.15.zip" -OutFile "$tmp\model-de.zip"
Expand-Archive "$tmp\vosk.zip" "$tmp\nuget"
Expand-Archive "$tmp\vosk-android.zip" "$tmp\aar"
Expand-Archive "$tmp\model.zip" "$tmp\model"
Expand-Archive "$tmp\model-de.zip" "$tmp\model"

New-Item -ItemType Directory -Force "$assets\Plugins\Vosk", "$assets\Plugins\x86_64", "$assets\Plugins\Android\libs\x86_64", "$assets\StreamingAssets" | Out-Null
Copy-Item "$tmp\nuget\lib\netstandard2.0\Vosk.dll" "$assets\Plugins\Vosk\"
Copy-Item "$tmp\nuget\build\lib\win-x64\*.dll" "$assets\Plugins\x86_64\"
Copy-Item "$tmp\aar\jni\x86_64\libvosk.so" "$assets\Plugins\Android\libs\x86_64\"   # Magic Leap 2 is x86-64
foreach ($name in "vosk-model-small-en-us-0.15", "vosk-model-small-de-0.15") {
    Copy-Item -Recurse -Force "$tmp\model\$name" "$assets\StreamingAssets\"
    # File list, so the headset can unpack the model from the APK on first start
    Push-Location "$assets\StreamingAssets\$name"
    Get-ChildItem -Recurse -File | Where-Object Name -ne "files.txt" |
        ForEach-Object { (Resolve-Path -Relative $_.FullName).Substring(2).Replace("\", "/") } |
        Sort-Object | Set-Content -Encoding ascii files.txt
    Pop-Location
}

Remove-Item -Recurse -Force $tmp
Write-Host "Done."
