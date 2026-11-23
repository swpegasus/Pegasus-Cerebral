Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "StrFunc.nsh"
${Using:StrFunc} StrStr
!searchparse /file "..\Directory.Build.props" "<Version>" CEREBRAL_VERSION "</Version>"

Name "Pegasus Cerebral Worker"
!ifndef PAO_OUT
  !define PAO_OUT "..\deploy\Cerebral-Worker-Setup.exe"
!endif
OutFile "${PAO_OUT}"
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES\Pegasus\Cerebral Worker"
SetCompressor lzma
CRCCheck on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${CEREBRAL_VERSION}.0"
VIAddVersionKey "ProductName" "Pegasus Cerebral Worker"
VIAddVersionKey "FileDescription" "Pegasus Cerebral standalone Worker installer"
VIAddVersionKey "FileVersion" "${CEREBRAL_VERSION}"
VIAddVersionKey "LegalCopyright" "Pegasus"

!define MUI_ICON "..\assets\branding\cerebral-setup.ico"
!define MUI_UNICON "..\assets\branding\cerebral-setup.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "..\assets\branding\cerebral\cerebral-installer-sidebar.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP_STRETCH FitControl
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install Pegasus Cerebral Worker ${CEREBRAL_VERSION}"
!define MUI_WELCOMEPAGE_TEXT "This installs only the background Worker used by another Pegasus Cerebral computer.$\r$\n$\r$\nSetup selects the native Windows payload for this computer. The Cerebral desktop remains an x64 application and is not included."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom WorkerProvisioningPageCreate WorkerProvisioningPageLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_NOAUTOCLOSE
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Var WorkerRid
Var WorkerSource
Var NativePowerShell
Var StateDir
Var UserDataDir
Var ExistingWorkerDir
Var ExistingWorkerMode
Var RemoteSelected
Var AutoPort
Var WorkerName
Var WorkerPort
Var AdvertisedHost
Var InvitationPath
Var SetupSummary
Var RemoteRadio
Var LocalRadio
Var StateInput
Var WorkerNameInput
Var WorkerPortInput
Var AutoPortCheckbox
Var AdvertisedHostInput
Var InvitationPathInput
Var RemoteFieldLabel

Function .onInit
  SetShellVarContext all
  # With the all-users shell context, $APPDATA resolves to ProgramData.
  StrCpy $StateDir "$APPDATA\Pegasus\Cerebral Worker"
  StrCpy $UserDataDir "$APPDATA\Pegasus\Cerebral\Users"
  ReadEnvStr $WorkerName "COMPUTERNAME"
  StrCpy $AdvertisedHost "$WorkerName"
  StrCpy $WorkerPort "2667"
  StrCpy $RemoteSelected 1
  StrCpy $AutoPort 1
  StrCpy $InvitationPath "$EXEDIR\Pegasus-$WorkerName.CerebralWorker"
  ReadRegStr $0 HKLM "Software\Pegasus\Cerebral" "InstallDir"
  ${If} $0 != ""
    MessageBox MB_ICONSTOP "The full Pegasus Cerebral x64 product already owns this computer's Worker. Use Cerebral Setup to update it." /SD IDOK
    Abort
  ${EndIf}
  ReadRegStr $ExistingWorkerDir HKLM "Software\Pegasus\CerebralWorker" "InstallDir"
  ${If} $ExistingWorkerDir != ""
    StrCpy $INSTDIR $ExistingWorkerDir
    ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "DataDir"
    ${If} $0 != ""
      StrCpy $StateDir $0
    ${EndIf}
    ReadRegStr $ExistingWorkerMode HKLM "Software\Pegasus\CerebralWorker" "Mode"
    ${If} $ExistingWorkerMode == ""
      StrCpy $ExistingWorkerMode "LocalOwner"
    ${EndIf}
    ${If} $ExistingWorkerMode == "RemoteManaged"
      StrCpy $RemoteSelected 1
      ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "WorkerName"
      ${If} $0 != ""
        StrCpy $WorkerName $0
      ${EndIf}
      ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "StartingPort"
      ${If} $0 != ""
        StrCpy $WorkerPort $0
      ${EndIf}
      ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "AdvertisedHost"
      ${If} $0 != ""
        StrCpy $AdvertisedHost $0
      ${EndIf}
      ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "InvitationPath"
      ${If} $0 != ""
        StrCpy $InvitationPath $0
      ${EndIf}
      ClearErrors
      ReadRegDWORD $0 HKLM "Software\Pegasus\CerebralWorker" "AutoPort"
      ${IfNot} ${Errors}
        StrCpy $AutoPort $0
      ${EndIf}
    ${Else}
      StrCpy $RemoteSelected 0
    ${EndIf}
  ${EndIf}
  ${If} ${IsNativeARM64}
    StrCpy $WorkerRid "win-arm64"
    ${If} $ExistingWorkerDir == ""
      StrCpy $INSTDIR "$PROGRAMFILES64\Pegasus\Cerebral Worker"
    ${EndIf}
    StrCpy $NativePowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  ${ElseIf} ${IsNativeAMD64}
    StrCpy $WorkerRid "win-x64"
    ${If} $ExistingWorkerDir == ""
      StrCpy $INSTDIR "$PROGRAMFILES64\Pegasus\Cerebral Worker"
    ${EndIf}
    StrCpy $NativePowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  ${ElseIf} ${IsNativeIA32}
    StrCpy $WorkerRid "win-x86"
    ${If} $ExistingWorkerDir == ""
      StrCpy $INSTDIR "$PROGRAMFILES32\Pegasus\Cerebral Worker"
    ${EndIf}
    StrCpy $NativePowerShell "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
  ${Else}
    MessageBox MB_ICONSTOP "This Worker installer supports Windows x64, Windows ARM64, and Windows x86." /SD IDOK
    Abort
  ${EndIf}
  StrCpy $WorkerSource "$EXEDIR\Workers\$WorkerRid"
  IfFileExists "$WorkerSource\Cerebral.Worker.exe" +3 0
    MessageBox MB_ICONSTOP "The adjacent $WorkerRid Worker payload is missing. Keep the Workers folder beside this installer." /SD IDOK
    Abort
  IfFileExists "$EXEDIR\Support\Install-CerebralWorker.ps1" +3 0
    MessageBox MB_ICONSTOP "The standalone Worker installation helper is missing. Keep the Support folder beside this installer." /SD IDOK
    Abort
  IfFileExists "$EXEDIR\Support\Register-CerebralOwnerPipe.ps1" +3 0
    MessageBox MB_ICONSTOP "The protected owner-pipe helper is missing. Keep the Support folder beside this installer." /SD IDOK
    Abort
FunctionEnd

Function WorkerProvisioningPageCreate
  !insertmacro MUI_HEADER_TEXT "Worker role and provisioning" "Choose how this Worker is reached and where its durable state is stored."
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 22u "A remote Worker is claimed once by Pegasus Cerebral Configuration. After assignment, setup preserves the controller and Configuration owns every mutable Worker setting."
  Pop $SetupSummary
  ${NSD_CreateRadioButton} 0 24u 100% 12u "Remote managed Worker — LAN service with one-time .CerebralWorker invitation"
  Pop $RemoteRadio
  ${NSD_OnClick} $RemoteRadio WorkerProvisioningRefresh
  ${NSD_CreateRadioButton} 0 39u 100% 12u "Local owner Worker — protected same-machine pipe with no network listener"
  Pop $LocalRadio
  ${NSD_OnClick} $LocalRadio WorkerProvisioningRefresh
  ${If} $RemoteSelected == 1
    ${NSD_SetState} $RemoteRadio ${BST_CHECKED}
  ${Else}
    ${NSD_SetState} $LocalRadio ${BST_CHECKED}
  ${EndIf}
  ${NSD_CreateLabel} 0 56u 25% 12u "Durable Worker data:"
  Pop $0
  ${NSD_CreateDirRequest} 26% 53u 74% 14u "$StateDir"
  Pop $StateInput
  ${NSD_CreateLabel} 0 76u 25% 12u "Worker name:"
  Pop $RemoteFieldLabel
  ${NSD_CreateText} 26% 73u 74% 14u "$WorkerName"
  Pop $WorkerNameInput
  ${NSD_CreateLabel} 0 96u 25% 12u "Starting port:"
  Pop $0
  ${NSD_CreateNumber} 26% 93u 20% 14u "$WorkerPort"
  Pop $WorkerPortInput
  ${NSD_CreateCheckbox} 49% 94u 51% 12u "Use next available port"
  Pop $AutoPortCheckbox
  ${NSD_SetState} $AutoPortCheckbox $AutoPort
  ${NSD_CreateLabel} 0 116u 25% 12u "LAN name or IPv4:"
  Pop $0
  ${NSD_CreateText} 26% 113u 74% 14u "$AdvertisedHost"
  Pop $AdvertisedHostInput
  ${NSD_CreateLabel} 0 136u 25% 12u "Invitation output:"
  Pop $0
  ${NSD_CreateText} 26% 133u 74% 14u "$InvitationPath"
  Pop $InvitationPathInput
  ${NSD_CreateLabel} 0 156u 100% 36u "The invitation contains a short-lived bootstrap credential. Import it into Configuration on the controlling Cerebral computer; Configuration replaces that credential during claim. No peer-execution grant is created."
  Pop $0
  ${If} $ExistingWorkerDir != ""
    EnableWindow $RemoteRadio 0
    EnableWindow $LocalRadio 0
    EnableWindow $StateInput 0
    ${NSD_SetText} $SetupSummary "Upgrade detected. The installed Worker role, durable data location, controller assignment and management credential are preserved. Use Pegasus Cerebral Configuration for later Worker changes."
  ${EndIf}
  Call WorkerProvisioningRefresh
  nsDialogs::Show
FunctionEnd

Function WorkerProvisioningRefresh
  ${NSD_GetState} $RemoteRadio $RemoteSelected
  ${If} $RemoteSelected == ${BST_CHECKED}
    StrCpy $RemoteSelected 1
    EnableWindow $WorkerNameInput 1
    EnableWindow $WorkerPortInput 1
    EnableWindow $AutoPortCheckbox 1
    EnableWindow $AdvertisedHostInput 1
    EnableWindow $InvitationPathInput 1
  ${Else}
    StrCpy $RemoteSelected 0
    EnableWindow $WorkerNameInput 0
    EnableWindow $WorkerPortInput 0
    EnableWindow $AutoPortCheckbox 0
    EnableWindow $AdvertisedHostInput 0
    EnableWindow $InvitationPathInput 0
  ${EndIf}
  ${If} $ExistingWorkerDir != ""
    EnableWindow $WorkerNameInput 0
    EnableWindow $WorkerPortInput 0
    EnableWindow $AutoPortCheckbox 0
    EnableWindow $AdvertisedHostInput 0
    EnableWindow $InvitationPathInput 0
  ${EndIf}
FunctionEnd

Function WorkerProvisioningPageLeave
  ${NSD_GetText} $StateInput $StateDir
  ${If} $StateDir == ""
    MessageBox MB_ICONEXCLAMATION "Choose a product-specific durable Worker data folder."
    Abort
  ${EndIf}
  ${GetRoot} "$StateDir" $0
  ${If} $StateDir == $0
    MessageBox MB_ICONEXCLAMATION "Choose a Worker data folder, not a drive root."
    Abort
  ${EndIf}
  ${If} $RemoteSelected == 1
    ${NSD_GetText} $WorkerNameInput $WorkerName
    ${NSD_GetText} $WorkerPortInput $WorkerPort
    ${NSD_GetState} $AutoPortCheckbox $AutoPort
    ${NSD_GetText} $AdvertisedHostInput $AdvertisedHost
    ${NSD_GetText} $InvitationPathInput $InvitationPath
    ${If} $WorkerName == ""
      MessageBox MB_ICONEXCLAMATION "Enter a name for this Worker."
      Abort
    ${EndIf}
    ${StrStr} $0 $WorkerName '$\"'
    ${If} $0 != ""
      MessageBox MB_ICONEXCLAMATION "Worker names cannot contain quotation marks."
      Abort
    ${EndIf}
    IntCmp $WorkerPort 1 port_too_low port_minimum_ok port_minimum_ok
    port_too_low:
      MessageBox MB_ICONEXCLAMATION "The starting port must be between 1 and 65535."
      Abort
    port_minimum_ok:
    IntCmp $WorkerPort 65535 port_valid port_valid port_too_high
    port_too_high:
      MessageBox MB_ICONEXCLAMATION "The starting port must be between 1 and 65535."
      Abort
    port_valid:
    ${If} $AdvertisedHost == ""
      MessageBox MB_ICONEXCLAMATION "Enter this computer's LAN DNS name or IPv4 address."
      Abort
    ${EndIf}
    ${If} $InvitationPath == ""
      MessageBox MB_ICONEXCLAMATION "Choose where setup should write the one-time Worker invitation."
      Abort
    ${EndIf}
    ${GetFileExt} "$InvitationPath" $0
    ${If} $0 != "CerebralWorker"
      StrCpy $InvitationPath "$InvitationPath.CerebralWorker"
      ${NSD_SetText} $InvitationPathInput "$InvitationPath"
    ${EndIf}
  ${EndIf}
FunctionEnd

Section "Worker" SecWorker
  SectionIn RO
  SetShellVarContext all
  ${If} $INSTDIR == ""
    Abort "Select a Worker installation directory."
  ${EndIf}
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
    Abort "Select a product installation directory, not a drive root."
  ${EndIf}

  IfFileExists "$INSTDIR\Cerebral.Worker.exe" 0 worker_stopped
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
    Pop $0
    ${If} $0 != 0
      Abort "The existing Worker could not stop. No files were replaced."
    ${EndIf}
  worker_stopped:

  CreateDirectory "$StateDir"
  nsExec::ExecToLog /TIMEOUT=1800000 '"$NativePowerShell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$EXEDIR\Support\Install-CerebralWorker.ps1" -SourceRoot "$WorkerSource" -DestinationRoot "$INSTDIR" -ReceiptPath "$StateDir\worker-installation.json"'
  Pop $0
  ${If} $0 != 0
    Abort "Worker file replacement failed. The prior generation was restored when possible."
  ${EndIf}

  ${If} $RemoteSelected == 1
    ${If} $AutoPort == ${BST_CHECKED}
      nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage provision-standalone --data "$StateDir" --name "$WorkerName" --listen-url "http://0.0.0.0:$WorkerPort" --auto-port'
    ${Else}
      nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage provision-standalone --data "$StateDir" --name "$WorkerName" --listen-url "http://0.0.0.0:$WorkerPort"'
    ${EndIf}
    Pop $0
    ${If} $0 != 0
      Abort "Remote Worker provisioning failed before service registration. The service remains stopped; review setup details and retry."
    ${EndIf}
    nsExec::ExecToLog /TIMEOUT=180000 '"$INSTDIR\Cerebral.Worker.exe" manage register-http-service --data "$StateDir"'
    Pop $0
    ${If} $0 != 0
      Abort "Remote Worker service registration failed. The service remains stopped; review setup details and retry."
    ${EndIf}
  ${Else}
    CreateDirectory "$UserDataDir"
    # Keep the registration helper alive until its original child finishes.
    nsExec::ExecToLog '"$NativePowerShell" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$EXEDIR\Support\Register-CerebralOwnerPipe.ps1" -WorkerPath "$INSTDIR\Cerebral.Worker.exe" -DataDirectory "$StateDir" -UserDataRoot "$UserDataDir" -ReceiptPath "$StateDir\worker-owner-pipe.json"'
    Pop $0
    ${If} $0 != 0
      Abort "Protected Worker registration failed. The service remains stopped; review setup details and retry."
    ${EndIf}
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage remove-firewall'
    Pop $0
    ${If} $0 != 0
      Abort "The local owner-pipe Worker was registered, but a prior remote Worker firewall rule could not be removed."
    ${EndIf}
  ${EndIf}
  nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage start'
  Pop $0
  ${If} $0 != 0
    Abort "The Worker service did not start. Review setup details and retry."
  ${EndIf}
  nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage verify --data "$StateDir" --expected-version "${CEREBRAL_VERSION}"'
  Pop $0
  ${If} $0 != 0
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
    Pop $1
    Abort "The Worker started but its protected local health/version check failed."
  ${EndIf}
  ${If} $RemoteSelected == 1
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage configure-firewall --data "$StateDir"'
    Pop $0
    ${If} $0 != 0
      nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
      Pop $1
      Abort "The Worker is healthy, but setup could not create its private-network firewall rule. The service was stopped."
    ${EndIf}
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage finalize-standalone --data "$StateDir" --advertised-host "$AdvertisedHost" --output "$InvitationPath"'
    Pop $0
    ${If} $0 != 0
      nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage remove-firewall'
      Pop $1
      nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
      Pop $1
      Abort "The Worker is running, but setup could not write its one-time invitation. Review setup details and retry."
    ${EndIf}
  ${EndIf}

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteINIStr "$INSTDIR\worker-installation.ini" "Worker" "Product" "PegasusCerebralWorker"
  WriteINIStr "$INSTDIR\worker-installation.ini" "Worker" "Version" "${CEREBRAL_VERSION}"
  WriteINIStr "$INSTDIR\worker-installation.ini" "Worker" "Runtime" "$WorkerRid"
  WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "DataDir" "$StateDir"
  WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "Version" "${CEREBRAL_VERSION}"
  WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "Runtime" "$WorkerRid"
  ${If} $RemoteSelected == 1
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "Mode" "RemoteManaged"
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "WorkerName" "$WorkerName"
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "StartingPort" "$WorkerPort"
    WriteRegDWORD HKLM "Software\Pegasus\CerebralWorker" "AutoPort" $AutoPort
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "AdvertisedHost" "$AdvertisedHost"
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "InvitationPath" "$InvitationPath"
  ${Else}
    WriteRegStr HKLM "Software\Pegasus\CerebralWorker" "Mode" "LocalOwner"
  ${EndIf}
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "DisplayName" "Pegasus Cerebral Worker"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "DisplayVersion" "${CEREBRAL_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "Publisher" "Pegasus"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  ReadRegStr $0 HKLM "Software\Pegasus\CerebralWorker" "InstallDir"
  ${If} $0 != "$INSTDIR"
    Abort "The registered standalone Worker directory does not match this uninstaller."
  ${EndIf}
  IfFileExists "$INSTDIR\Cerebral.Worker.exe" 0 remove_files
  nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
  Pop $0
  nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage remove-firewall'
  Pop $0
  ${If} $0 != 0
    Abort "The Worker firewall rule could not be removed. Uninstall stopped before deleting its owner executable."
  ${EndIf}
  nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage remove-service'
  Pop $0
  remove_files:
  DeleteRegKey HKLM "Software\Pegasus\CerebralWorker"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebralWorker"
  RMDir /r "$INSTDIR"
  # Service data and models are deliberately preserved for repair or a later Worker generation.
SectionEnd
