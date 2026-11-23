Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "TextFunc.nsh"
!include "Sections.nsh"
!include "WinMessages.nsh"
!include "StrFunc.nsh"
!ifndef CEREBRAL_SKIP_CODEX_PREFLIGHT
  ${Using:StrFunc} StrStr
!endif
!searchparse /file "..\Directory.Build.props" "<Version>" CEREBRAL_VERSION "</Version>"

Name "Pegasus Cerebral"
!ifndef PAO_OUT
  !define PAO_OUT "..\deploy\Cerebral-Setup.exe"
!endif
OutFile "${PAO_OUT}"
InstallDir ""
!ifdef CEREBRAL_UI_TEST
  RequestExecutionLevel user
!else
  RequestExecutionLevel admin
!endif
SetCompressor lzma
CRCCheck on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${CEREBRAL_VERSION}.0"
VIAddVersionKey "ProductName" "Pegasus Cerebral"
VIAddVersionKey "FileDescription" "Pegasus Cerebral offline installer"
VIAddVersionKey "FileVersion" "${CEREBRAL_VERSION}"
VIAddVersionKey "LegalCopyright" "Pegasus"

# Icons are source assets; packaging remains an explicit install/update action.
!define MUI_ICON "..\assets\branding\cerebral-setup.ico"
!define MUI_UNICON "..\assets\branding\cerebral-setup.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "..\assets\branding\cerebral\cerebral-installer-sidebar.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP_STRETCH FitControl
!define MUI_ABORTWARNING
!define MUI_ABORTWARNING_CANCEL_DEFAULT
!define MUI_ABORTWARNING_TEXT "Cancel Pegasus Cerebral Setup? No installation changes have been applied on the selection pages."
!define MUI_CUSTOMFUNCTION_GUIINIT ConfigureInstallerWindow

!ifndef WS_SIZEBOX
  !define WS_SIZEBOX 0x00040000
!endif
!ifndef WS_MAXIMIZEBOX
  !define WS_MAXIMIZEBOX 0x00010000
!endif
!ifndef WS_MINIMIZEBOX
  !define WS_MINIMIZEBOX 0x00020000
!endif
!ifndef GWL_STYLE
  !define GWL_STYLE -16
!endif
!ifndef SC_CLOSE
  !define SC_CLOSE 0xF060
!endif
!ifndef MF_ENABLED
  !define MF_ENABLED 0x00000000
!endif
!ifndef MF_GRAYED
  !define MF_GRAYED 0x00000001
!endif

!macro MoveInstallerControl Handle X Y Width Height
  System::Call 'user32::IsWindow(p ${Handle}) i .R9'
  ${If} $R9 != 0
    System::Call 'user32::SetWindowPos(p ${Handle}, p 0, i ${X}, i ${Y}, i ${Width}, i ${Height}, i 0x14)'
  ${EndIf}
!macroend

Var Portable
Var Args
Var BaseDir
Var InstallInput
Var InstallBrowseButton
Var InstallTitleLabel
Var BaseInput
Var BaseBrowseButton
Var BaseIntroLabel
Var BaseTitleLabel
Var StateDir
Var ModelsDir
Var ToolsDir
Var SharedDir
Var UserDataDir
Var ProjectsDir
Var ProjectsInput
Var ProjectsBrowseButton
Var ProjectsTitleLabel
Var ProjectsSpecified
Var DesktopShortcuts
Var DesktopCheckbox
Var LocationsLabel
Var ExistingLayout
Var CortexCheckDirectory
Var CortexCheckDataDirectory
Var CortexInstallAttempted
Var CortexInstallerArguments
Var ModelFindHandle
Var ModelFindName
Var ModelPackageCount
Var ComponentsInitialized
Var CacheInstallationSourceRequested
Var CortexBehaviorPacksRequested
Var BundledPluginsRequested
Var SkipPythonInstallRequested
Var SkipCortexInstallRequested
Var PreserveWorkerModels
Var EarlyGateDiagnosticsStarted
Var ModelSummaryLabel
Var ModelSpaceLabel
Var ModelPackage0
Var ModelPackage1
Var ModelPackage2
Var ModelPackage3
Var ModelPackage4
Var ModelPackage5
Var ModelPackage6
Var ModelPackage7
Var ModelSize0
Var ModelSize1
Var ModelSize2
Var ModelSize3
Var ModelSize4
Var ModelSize5
Var ModelSize6
Var ModelSize7
Var ModelSelected0
Var ModelSelected1
Var ModelSelected2
Var ModelSelected3
Var ModelSelected4
Var ModelSelected5
Var ModelSelected6
Var ModelSelected7
Var ModelInstalled0
Var ModelInstalled1
Var ModelInstalled2
Var ModelInstalled3
Var ModelInstalled4
Var ModelInstalled5
Var ModelInstalled6
Var ModelInstalled7
Var ModelCheckbox0
Var ModelCheckbox1
Var ModelCheckbox2
Var ModelCheckbox3
Var ModelCheckbox4
Var ModelCheckbox5
Var ModelCheckbox6
Var ModelCheckbox7
Var PrerequisiteComponents
Var PrerequisiteForceComponents
Var NativePowerShell
Var InstallationSourceCache
Var CorePayloadDirectory
Var CoreWorkerPath
Var InstallerLayoutTimerStarted
Var InstallerMinimumWidth
Var InstallerMinimumHeight
Var InstallerLastClientWidth
Var InstallerLastClientHeight
Var InstallerLastPageWindow
Var InstallerInstallRunning
Var InstallerStateFile

!define MUI_WELCOMEPAGE_TITLE "Install Pegasus Cerebral ${CEREBRAL_VERSION}"
!define MUI_WELCOMEPAGE_TEXT "Setup will install the Cerebral desktop, its local worker and the components you choose.$\r$\n$\r$\nYou will review storage locations, capabilities, development tools, model packages and recovery options before installation begins.$\r$\n$\r$\nNo system changes are made until the final Install step."
!define MUI_PAGE_CUSTOMFUNCTION_SHOW InstallerPageShown
!insertmacro MUI_PAGE_WELCOME
Page custom ProjectsPageCreate ProjectsPageLeave
!define MUI_PAGE_CUSTOMFUNCTION_PRE PrepareComponentsPage
!define MUI_PAGE_CUSTOMFUNCTION_SHOW InstallerPageShown
!insertmacro MUI_PAGE_COMPONENTS
Page custom ModelsPageCreate ModelsPageLeave
!define MUI_PAGE_CUSTOMFUNCTION_SHOW InstallFilesPageShown
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open Pegasus Cerebral Configuration"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenConfigurationAfterInstall
!define MUI_PAGE_CUSTOMFUNCTION_SHOW ConfigureFinishPage
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function SetInstallerCancellationEnabled
  System::Store "S"
  GetDlgItem $0 $HWNDPARENT 2
  System::Call 'user32::IsWindow(p r0) i .r1'
  ${If} $1 != 0
    EnableWindow $0 1
  ${EndIf}
  System::Call 'user32::GetSystemMenu(p $HWNDPARENT, i 0) p .r0'
  ${If} $0 != 0
    System::Call 'user32::EnableMenuItem(p r0, i ${SC_CLOSE}, i ${MF_ENABLED})'
    System::Call 'user32::DrawMenuBar(p $HWNDPARENT)'
  ${EndIf}
  System::Store "L"
FunctionEnd

Function SetInstallerCancellationDisabled
  System::Store "S"
  GetDlgItem $0 $HWNDPARENT 2
  System::Call 'user32::IsWindow(p r0) i .r1'
  ${If} $1 != 0
    EnableWindow $0 0
  ${EndIf}
  System::Call 'user32::GetSystemMenu(p $HWNDPARENT, i 0) p .r0'
  ${If} $0 != 0
    System::Call 'user32::EnableMenuItem(p r0, i ${SC_CLOSE}, i ${MF_GRAYED})'
    System::Call 'user32::DrawMenuBar(p $HWNDPARENT)'
  ${EndIf}
  System::Store "L"
FunctionEnd

Function ConfigureInstallerWindow
  StrCpy $InstallerLayoutTimerStarted 0
  StrCpy $InstallerInstallRunning 0
  StrCpy $InstallerLastClientWidth -1
  StrCpy $InstallerLastClientHeight -1
  StrCpy $InstallerLastPageWindow 0

  # Use an ordinary resizable Windows frame. The default is 1024x768 when the
  # monitor work area permits it; a smaller work area remains fully reachable.
  System::Call 'user32::GetWindowLongPtr(p $HWNDPARENT, i ${GWL_STYLE}) p .r0'
  IntOp $0 $0 | ${WS_SIZEBOX}
  IntOp $0 $0 | ${WS_MAXIMIZEBOX}
  IntOp $0 $0 | ${WS_MINIMIZEBOX}
  System::Call 'user32::SetWindowLongPtr(p $HWNDPARENT, i ${GWL_STYLE}, p r0)'

  System::Call 'user32::SystemParametersInfoW(i 0x30, i 0, @r0, i 0) i .r5'
  ${If} $5 != 0
    System::Call '*$0(i.r1, i.r2, i.r3, i.r4)'
  ${Else}
    StrCpy $1 0
    StrCpy $2 0
    StrCpy $3 1024
    StrCpy $4 768
  ${EndIf}
  IntOp $5 $3 - $1
  IntOp $6 $4 - $2
  StrCpy $InstallerMinimumWidth 1024
  StrCpy $InstallerMinimumHeight 768
  ${If} $5 < $InstallerMinimumWidth
    StrCpy $InstallerMinimumWidth $5
  ${EndIf}
  ${If} $6 < $InstallerMinimumHeight
    StrCpy $InstallerMinimumHeight $6
  ${EndIf}
  IntOp $7 $5 - $InstallerMinimumWidth
  IntOp $7 $7 / 2
  IntOp $7 $7 + $1
  IntOp $8 $6 - $InstallerMinimumHeight
  IntOp $8 $8 / 2
  IntOp $8 $8 + $2
  System::Call 'user32::SetWindowPos(p $HWNDPARENT, p 0, i r7, i r8, i $InstallerMinimumWidth, i $InstallerMinimumHeight, i 0x34)'

  StrCpy $InstallerLayoutTimerStarted 1
  ${NSD_CreateTimer} LayoutInstallerWindow 100
  Call LayoutInstallerWindow
FunctionEnd

Function InstallerPageShown
  StrCpy $InstallerInstallRunning 0
  Call SetInstallerCancellationEnabled
  StrCpy $InstallerLastClientWidth -1
  Call LayoutInstallerWindow
FunctionEnd

Function InstallFilesPageShown
  StrCpy $InstallerInstallRunning 1
  Call SetInstallerCancellationDisabled
  StrCpy $InstallerLastClientWidth -1
  Call LayoutInstallerWindow
FunctionEnd

Function LayoutInstallerWindow
  System::Store "S"
  System::Call 'user32::IsIconic(p $HWNDPARENT) i .r0'
  ${If} $0 != 0
    Goto installer_layout_done
  ${EndIf}

  # Enforce the usable minimum while allowing maximize and larger owner sizes.
  System::Call 'user32::IsZoomed(p $HWNDPARENT) i .r0'
  ${If} $0 == 0
    System::Call 'user32::GetWindowRect(p $HWNDPARENT, @r0)'
    System::Call '*$0(i.r1, i.r2, i.r3, i.r4)'
    IntOp $5 $3 - $1
    IntOp $6 $4 - $2
    StrCpy $7 $5
    StrCpy $8 $6
    ${If} $7 < $InstallerMinimumWidth
      StrCpy $7 $InstallerMinimumWidth
    ${EndIf}
    ${If} $8 < $InstallerMinimumHeight
      StrCpy $8 $InstallerMinimumHeight
    ${EndIf}
    ${If} $7 != $5
      System::Call 'user32::SetWindowPos(p $HWNDPARENT, p 0, i 0, i 0, i r7, i r8, i 0x16)'
    ${ElseIf} $8 != $6
      System::Call 'user32::SetWindowPos(p $HWNDPARENT, p 0, i 0, i 0, i r7, i r8, i 0x16)'
    ${EndIf}
  ${EndIf}

  System::Call 'user32::GetClientRect(p $HWNDPARENT, @r0)'
  System::Call '*$0(i, i, i.r1, i.r2)'
  GetDlgItem $3 $HWNDPARENT 0
  ${If} $InstallerLastClientWidth == $1
  ${AndIf} $InstallerLastClientHeight == $2
  ${AndIf} $InstallerLastPageWindow == $3
    Goto installer_layout_cancel_state
  ${EndIf}
  StrCpy $InstallerLastClientWidth $1
  StrCpy $InstallerLastClientHeight $2
  StrCpy $InstallerLastPageWindow $3

  LockWindow on
  GetDlgItem $4 $HWNDPARENT 1044
  IntOp $5 $2 - 76
  !insertmacro MoveInstallerControl $4 0 0 $1 $5
  GetDlgItem $4 $HWNDPARENT 1034
  !insertmacro MoveInstallerControl $4 0 0 $1 57
  GetDlgItem $4 $HWNDPARENT 1036
  !insertmacro MoveInstallerControl $4 0 57 $1 2
  GetDlgItem $4 $HWNDPARENT 1037
  IntOp $5 $1 - 94
  !insertmacro MoveInstallerControl $4 15 8 $5 18
  GetDlgItem $4 $HWNDPARENT 1038
  IntOp $5 $1 - 104
  !insertmacro MoveInstallerControl $4 23 28 $5 24
  GetDlgItem $4 $HWNDPARENT 1039
  IntOp $5 $1 - 58
  !insertmacro MoveInstallerControl $4 $5 13 32 32

  IntOp $5 $1 - 46
  GetDlgItem $4 $HWNDPARENT 1018
  StrCpy $9 0
  System::Call 'user32::IsWindow(p $mui.WelcomePage.Image) i .r0'
  ${If} $0 != 0
    StrCpy $9 1
  ${EndIf}
  System::Call 'user32::IsWindow(p $mui.FinishPage.Image) i .r0'
  ${If} $0 != 0
    StrCpy $9 1
  ${EndIf}
  ${If} $9 == 1
    IntOp $6 $2 - 108
    !insertmacro MoveInstallerControl $4 23 23 $5 $6
    !insertmacro MoveInstallerControl $3 23 23 $5 $6
  ${Else}
    IntOp $6 $2 - 158
    !insertmacro MoveInstallerControl $4 23 73 $5 $6
    !insertmacro MoveInstallerControl $3 23 73 $5 $6
  ${EndIf}

  # Keep the footer visually separate from the navigation row. The previous
  # legacy coordinates put the buttons directly on the horizontal rule.
  IntOp $5 $2 - 65
  IntOp $6 $1 - 16
  GetDlgItem $4 $HWNDPARENT 1035
  !insertmacro MoveInstallerControl $4 8 $5 $6 2
  GetDlgItem $4 $HWNDPARENT 1045
  !insertmacro MoveInstallerControl $4 0 $5 $1 2
  IntOp $5 $2 - 47
  GetDlgItem $4 $HWNDPARENT 1256
  !insertmacro MoveInstallerControl $4 8 $5 $6 13
  GetDlgItem $4 $HWNDPARENT 1028
  !insertmacro MoveInstallerControl $4 8 $5 $6 13

  IntOp $5 $2 - 50
  IntOp $6 $1 - 86
  GetDlgItem $4 $HWNDPARENT 2
  !insertmacro MoveInstallerControl $4 $6 $5 75 23
  IntOp $6 $6 - 86
  GetDlgItem $4 $HWNDPARENT 1
  !insertmacro MoveInstallerControl $4 $6 $5 75 23
  IntOp $6 $6 - 75
  GetDlgItem $4 $HWNDPARENT 3
  !insertmacro MoveInstallerControl $4 $6 $5 75 23

  System::Call 'user32::IsWindow(p r3) i .r0'
  ${If} $0 != 0
    System::Call 'user32::GetClientRect(p r3, @r0)'
    System::Call '*$0(i, i, i.r4, i.r5)'

    # Welcome and finish are full-window pages. Give the product artwork the
    # full usable height and align the copy as a proper two-column splash.
    System::Call 'user32::IsWindow(p $mui.WelcomePage.Image) i .r0'
    ${If} $0 != 0
      IntOp $6 $4 * 31
      IntOp $6 $6 / 100
      !insertmacro MoveInstallerControl $mui.WelcomePage.Image 0 0 $6 $5
      IntOp $7 $6 + 34
      IntOp $8 $4 - $7
      !insertmacro MoveInstallerControl $mui.WelcomePage.Title $7 30 $8 58
      IntOp $9 $5 - 122
      !insertmacro MoveInstallerControl $mui.WelcomePage.Text $7 108 $8 $9
    ${EndIf}
    System::Call 'user32::IsWindow(p $mui.FinishPage.Image) i .r0'
    ${If} $0 != 0
      IntOp $6 $4 * 31
      IntOp $6 $6 / 100
      !insertmacro MoveInstallerControl $mui.FinishPage.Image 0 0 $6 $5
      IntOp $7 $6 + 34
      IntOp $8 $4 - $7
      !insertmacro MoveInstallerControl $mui.FinishPage.Title $7 30 $8 58
      IntOp $9 $5 - 122
      !insertmacro MoveInstallerControl $mui.FinishPage.Text $7 108 $8 $9
    ${EndIf}

    # The standard Components page receives the extra room instead of retaining
    # the legacy 147-pixel tree and placeholder description panel.
    GetDlgItem $6 $3 1032
    System::Call 'user32::IsWindow(p r6) i .r0'
    ${If} $0 != 0
      GetDlgItem $7 $3 1006
      !insertmacro MoveInstallerControl $7 0 0 $4 41
      GetDlgItem $7 $3 1021
      !insertmacro MoveInstallerControl $7 0 48 150 20
      IntOp $8 $4 - 160
      GetDlgItem $7 $3 1017
      !insertmacro MoveInstallerControl $7 160 45 $8 24
      GetDlgItem $7 $3 1022
      !insertmacro MoveInstallerControl $7 0 80 $4 20
      IntOp $7 $4 * 65
      IntOp $7 $7 / 100
      IntOp $7 $7 - 8
      IntOp $8 $5 - 140
      !insertmacro MoveInstallerControl $6 0 105 $7 $8
      GetDlgItem $6 $3 1023
      IntOp $8 $5 - 31
      !insertmacro MoveInstallerControl $6 0 $8 $7 25
      IntOp $8 $7 + 15
      IntOp $6 $4 - $8
      GetDlgItem $7 $3 1042
      IntOp $9 $5 - 106
      !insertmacro MoveInstallerControl $7 $8 99 $6 $9
      GetDlgItem $7 $3 1043
      IntOp $8 $8 + 9
      IntOp $6 $6 - 18
      IntOp $9 $5 - 132
      !insertmacro MoveInstallerControl $7 $8 118 $6 $9
    ${EndIf}

    # Custom location page.
    System::Call 'user32::IsWindow(p $BaseInput) i .r0'
    ${If} $0 != 0
      !insertmacro MoveInstallerControl $BaseIntroLabel 0 0 $4 32
      !insertmacro MoveInstallerControl $InstallTitleLabel 0 38 $4 20
      IntOp $6 $4 - 140
      !insertmacro MoveInstallerControl $InstallInput 0 58 $6 26
      IntOp $6 $4 - 130
      !insertmacro MoveInstallerControl $InstallBrowseButton $6 57 130 28
      !insertmacro MoveInstallerControl $BaseTitleLabel 0 90 $4 20
      IntOp $6 $4 - 140
      !insertmacro MoveInstallerControl $BaseInput 0 110 $6 26
      IntOp $6 $4 - 130
      !insertmacro MoveInstallerControl $BaseBrowseButton $6 109 130 28
      !insertmacro MoveInstallerControl $ProjectsTitleLabel 0 142 $4 20
      IntOp $6 $4 - 140
      !insertmacro MoveInstallerControl $ProjectsInput 0 162 $6 26
      IntOp $6 $4 - 130
      !insertmacro MoveInstallerControl $ProjectsBrowseButton $6 161 130 28
      IntOp $6 $5 - 248
      !insertmacro MoveInstallerControl $LocationsLabel 0 204 $4 $6
      IntOp $6 $5 - 30
      !insertmacro MoveInstallerControl $DesktopCheckbox 0 $6 $4 24
    ${EndIf}

    # Custom model page. Checkboxes retain simple names while the summary and
    # storage calculation use the full available width.
    System::Call 'user32::IsWindow(p $ModelSummaryLabel) i .r0'
    ${If} $0 != 0
      !insertmacro MoveInstallerControl $ModelSummaryLabel 0 0 $4 42
      !insertmacro MoveInstallerControl $ModelCheckbox0 0 60 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox1 0 92 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox2 0 124 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox3 0 156 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox4 0 188 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox5 0 220 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox6 0 252 $4 24
      !insertmacro MoveInstallerControl $ModelCheckbox7 0 284 $4 24
      IntOp $6 $5 - 42
      !insertmacro MoveInstallerControl $ModelSpaceLabel 0 $6 $4 36
    ${EndIf}

    # Standard install-details page.
    GetDlgItem $6 $3 1016
    System::Call 'user32::IsWindow(p r6) i .r0'
    ${If} $0 != 0
      GetDlgItem $7 $3 1006
      !insertmacro MoveInstallerControl $7 0 0 $4 22
      GetDlgItem $7 $3 1004
      !insertmacro MoveInstallerControl $7 0 30 $4 18
      IntOp $8 $5 - 92
      !insertmacro MoveInstallerControl $6 0 58 $4 $8
      GetDlgItem $7 $3 1027
      IntOp $8 $5 - 28
      !insertmacro MoveInstallerControl $7 0 $8 130 24
    ${EndIf}
  ${EndIf}
  LockWindow off

  installer_layout_cancel_state:
  # NSIS re-enables Cancel after a failed Section. Detect that transition and
  # restore Close so a failed setup never leaves the owner trapped in the page.
  ${If} $InstallerInstallRunning == 1
    GetDlgItem $0 $HWNDPARENT 2
    System::Call 'user32::IsWindowEnabled(p r0) i .r1'
    ${If} $1 != 0
      StrCpy $InstallerInstallRunning 0
      Call SetInstallerCancellationEnabled
    ${EndIf}
  ${EndIf}

  installer_layout_done:
  System::Store "L"
FunctionEnd

# Portable and silent extraction never start a GUI.
Function ConfigureFinishPage
  StrCpy $InstallerInstallRunning 0
  Call SetInstallerCancellationEnabled
  StrCpy $InstallerLastClientWidth -1
  Call LayoutInstallerWindow
  ${If} $Portable == 1
    ${NSD_SetState} $mui.FinishPage.Run 0
    ShowWindow $mui.FinishPage.Run 0
  ${EndIf}
FunctionEnd

Function OpenConfigurationAfterInstall
  IfSilent done_open_configuration
  ${If} $Portable == 1
    Goto done_open_configuration
  ${EndIf}
  nsExec::ExecToStack /TIMEOUT=30000 '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage launch-configuration --directory "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "Cerebral was installed, but Configuration could not open in your ordinary user session. Open Pegasus Cerebral Configuration from the Start menu.$\r$\n$\r$\n$1" /SD IDOK
  ${EndIf}
  done_open_configuration:
FunctionEnd

# Import one external model package through the same Worker command used by
# Configuration. $4 is the package path. The package remains in the release
# folder; only the bounded staging copy is removed after the service consumes it.
Function ImportModelPackage
  CreateDirectory "$StateDir\Incoming"
  ${GetFileName} "$4" $3
  ClearErrors
  CopyFiles /SILENT "$4" "$StateDir\Incoming"
  ${If} ${Errors}
    MessageBox MB_ICONEXCLAMATION "Pegasus Cerebral is installed and running, but model package '$3' could not be staged. It remains in the release source and can be imported from Configuration." /SD IDOK
    Return
  ${EndIf}
  nsExec::ExecToStack /TIMEOUT=1800000 '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage install-model-package --data "$StateDir" --package "$StateDir\Incoming\$3"'
  Pop $0
  Pop $1
  Delete "$StateDir\Incoming\$3"
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION "Pegasus Cerebral is installed and running, but model package '$3' could not be loaded and configured. It remains in the release source and can be imported from Configuration.$\r$\n$\r$\n$1" /SD IDOK
  ${EndIf}
FunctionEnd

Function ResolveProjectPath
  ${If} $ProjectsDir == ""
    nsExec::ExecToStack /TIMEOUT=30000 '"$CoreWorkerPath" manage project-path --data "$StateDir" --directory "$INSTDIR"'
  ${Else}
    nsExec::ExecToStack /TIMEOUT=30000 '"$CoreWorkerPath" manage project-path --data "$StateDir" --directory "$INSTDIR" --projects "$ProjectsDir"'
  ${EndIf}
  Pop $0
  Pop $1
  ${If} $EarlyGateDiagnosticsStarted == 1
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "ProjectPathExit" "$0"
  ${EndIf}
  ${If} $0 != 0
    ${If} $EarlyGateDiagnosticsStarted == 1
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "project-path-failed"
    ${EndIf}
    MessageBox MB_ICONSTOP "The project location could not be validated. Choose a dedicated folder outside program files and Cortex. Existing project files are not moved.$\r$\n$\r$\n$1" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
  ${TrimNewLines} "$1" $ProjectsDir
FunctionEnd

Function ProjectsPageCreate
  ${If} $Portable == 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Cerebral locations" "Choose the program, writable data, and agent workspace locations."
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 24u "Choose three independent locations. Program files are replaceable; writable data and agent projects survive application updates."
  Pop $BaseIntroLabel
  ${NSD_CreateLabel} 0 28u 100% 12u "Program location:"
  Pop $InstallTitleLabel
  ${NSD_CreateDirRequest} 0 43u 78% 14u "$INSTDIR"
  Pop $InstallInput
  ${NSD_OnChange} $InstallInput RefreshLocations
  ${NSD_CreateBrowseButton} 80% 42u 20% 16u "Browse..."
  Pop $InstallBrowseButton
  ${NSD_OnClick} $InstallBrowseButton BrowseProgram
  ${NSD_CreateLabel} 0 64u 100% 12u "Writable data location:"
  Pop $BaseTitleLabel
  ${NSD_CreateDirRequest} 0 79u 78% 14u "$BaseDir"
  Pop $BaseInput
  ${NSD_OnChange} $BaseInput RefreshLocations
  ${NSD_CreateBrowseButton} 80% 78u 20% 16u "Browse..."
  Pop $BaseBrowseButton
  ${NSD_OnClick} $BaseBrowseButton BrowseData
  ${NSD_CreateLabel} 0 100u 100% 12u "Agent workspace root:"
  Pop $ProjectsTitleLabel
  ${NSD_CreateDirRequest} 0 115u 78% 14u "$ProjectsDir"
  Pop $ProjectsInput
  ${NSD_OnChange} $ProjectsInput RefreshLocations
  ${NSD_CreateBrowseButton} 80% 114u 20% 16u "Browse..."
  Pop $ProjectsBrowseButton
  ${NSD_OnClick} $ProjectsBrowseButton BrowseProjects
  ${NSD_CreateLabel} 0 136u 100% 54u ""
  Pop $LocationsLabel
  ${NSD_CreateCheckbox} 0 194u 100% 14u "Create desktop shortcuts for Pegasus Cerebral and Pegasus Cerebral Configuration"
  Pop $DesktopCheckbox
  ${NSD_SetState} $DesktopCheckbox $DesktopShortcuts
  ${If} $ExistingLayout == 1
    EnableWindow $InstallInput 0
    EnableWindow $InstallBrowseButton 0
    EnableWindow $BaseInput 0
    EnableWindow $BaseBrowseButton 0
  ${EndIf}
  Call RefreshLocations
  StrCpy $InstallerLastClientWidth -1
  Call LayoutInstallerWindow
  nsDialogs::Show
FunctionEnd

Function BrowseProgram
  Pop $0
  ${NSD_GetText} $InstallInput $INSTDIR
  nsDialogs::SelectFolderDialog "Choose the Pegasus Cerebral program folder" "$INSTDIR"
  Pop $0
  ${If} $0 != error
    ${NSD_SetText} $InstallInput "$0"
  ${EndIf}
FunctionEnd

Function BrowseData
  Pop $0
  ${NSD_GetText} $BaseInput $BaseDir
  nsDialogs::SelectFolderDialog "Choose the Pegasus Cerebral writable data folder" "$BaseDir"
  Pop $0
  ${If} $0 != error
    ${NSD_SetText} $BaseInput "$0"
  ${EndIf}
FunctionEnd

Function BrowseProjects
  Pop $0
  ${NSD_GetText} $ProjectsInput $ProjectsDir
  nsDialogs::SelectFolderDialog "Choose the Cerebral agent workspace root" "$ProjectsDir"
  Pop $0
  ${If} $0 != error
    ${NSD_SetText} $ProjectsInput "$0"
  ${EndIf}
FunctionEnd

Function RefreshLocations
  ${If} $ExistingLayout == 0
    ${NSD_GetText} $InstallInput $INSTDIR
    ${NSD_GetText} $BaseInput $BaseDir
    StrCpy $StateDir "$BaseDir"
    StrCpy $ModelsDir "$BaseDir\Models"
    StrCpy $ToolsDir "$BaseDir\Tools"
    StrCpy $SharedDir "$BaseDir\Shared"
    StrCpy $UserDataDir "$BaseDir\Users"
  ${EndIf}
  ${NSD_GetText} $ProjectsInput $ProjectsDir
  ${NSD_SetText} $LocationsLabel "Program: $INSTDIR$\r$\nDatabase, configuration and logs: $StateDir$\r$\nModels: $ModelsDir$\r$\nPortable tools: $ToolsDir$\r$\nProjects: $ProjectsDir$\r$\nShared files: $SharedDir$\r$\nPer-user profiles, plugin packages and plugin data: $UserDataDir"
FunctionEnd

Function ProjectsPageLeave
  Call RefreshLocations
  ${NSD_GetState} $DesktopCheckbox $DesktopShortcuts
  ${If} $BaseDir == ""
    MessageBox MB_ICONEXCLAMATION "Choose a Pegasus Cerebral writable data folder."
    Abort
  ${EndIf}
  ${If} $INSTDIR == ""
    MessageBox MB_ICONEXCLAMATION "Choose a Pegasus Cerebral program folder."
    Abort
  ${EndIf}
  ${If} $ProjectsDir == ""
    MessageBox MB_ICONEXCLAMATION "Choose a dedicated Cerebral agent workspace root."
    Abort
  ${EndIf}
  ${GetRoot} "$BaseDir" $0
  ${If} $BaseDir == $0
    MessageBox MB_ICONEXCLAMATION "Choose a product-specific writable data folder, not a drive root."
    Abort
  ${EndIf}
  StrCpy $ProjectsSpecified 1
  !ifndef CEREBRAL_UI_TEST
    Call ResolveProjectPath
  !endif
FunctionEnd

# The bundled read-only checker shares discovery/admission rules with the applications.
# Cortex remains a separately versioned product, but Cerebral treats its core as a required
# prerequisite and runs the adjacent installer without opening a second wizard.
Function RequireCortexPrerequisite
  Call CheckCortexPrerequisite
  ${If} $EarlyGateDiagnosticsStarted == 1
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexCheckExit" "$0"
  ${EndIf}
  ${If} $0 != 0
    ${If} $EarlyGateDiagnosticsStarted == 1
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-check-failed"
    ${EndIf}
    MessageBox MB_ICONSTOP "Pegasus Cortex did not satisfy Cerebral's prerequisite check after its silent install or upgrade. Cerebral files have not been replaced.$\r$\n$\r$\nCheck result: $0$\r$\n$1" /SD IDOK
    SetErrorLevel 1603
    Abort
  ${EndIf}
FunctionEnd

Function CheckCortexPrerequisite
  ${If} $CortexCheckDirectory == ""
    nsExec::ExecToStack /TIMEOUT=30000 '"$CoreWorkerPath" manage cortex-prerequisite'
  ${ElseIf} $CortexCheckDataDirectory == ""
    nsExec::ExecToStack /TIMEOUT=30000 '"$CoreWorkerPath" manage cortex-prerequisite --directory "$CortexCheckDirectory"'
  ${Else}
    nsExec::ExecToStack /TIMEOUT=30000 '"$CoreWorkerPath" manage cortex-prerequisite --directory "$CortexCheckDirectory" --data-directory "$CortexCheckDataDirectory"'
  ${EndIf}
  Pop $0
  Pop $1
FunctionEnd

Function LoadInstallerState
  StrCpy $InstallerStateFile ""
  IfFileExists "$EXEDIR\kit.ini" 0 installer_state_done
  IfFileExists "$EXEDIR\Support\Get-CerebralInstallerState.ps1" 0 installer_state_done
  GetTempFileName $InstallerStateFile
  nsExec::ExecToStack /TIMEOUT=60000 '"$NativePowerShell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$EXEDIR\Support\Get-CerebralInstallerState.ps1" -KitPath "$EXEDIR\kit.ini" -InstallDirectory "$INSTDIR" -OutputPath "$InstallerStateFile"'
  Pop $0
  Pop $1
  ${If} $0 != 0
    Delete "$InstallerStateFile"
    StrCpy $InstallerStateFile ""
  ${EndIf}
  installer_state_done:
FunctionEnd

Function .onGUIEnd
  ${If} $InstallerStateFile != ""
    Delete "$InstallerStateFile"
  ${EndIf}
FunctionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "This installer requires Windows x64." /SD IDOK
    SetErrorLevel 1633
    Quit
  ${EndIf}
  SetRegView 64
  # Keep the signed bootstrap small. Core application files remain in one adjacent,
  # uncompressed payload so setup can validate paths before replacing installed files.
  StrCpy $CorePayloadDirectory "$EXEDIR\Core\win-x64"
  StrCpy $CoreWorkerPath "$CorePayloadDirectory\Applications\Worker\Cerebral.Worker.exe"
  IfFileExists "$CoreWorkerPath" core_payload_ready 0
    # Developer build output keeps the same payload at deploy\win-x64. Released kits
    # always use Core\win-x64.
    StrCpy $CorePayloadDirectory "$EXEDIR\win-x64"
    StrCpy $CoreWorkerPath "$CorePayloadDirectory\Applications\Worker\Cerebral.Worker.exe"
  core_payload_ready:
  IfFileExists "$CoreWorkerPath" +4 0
    MessageBox MB_ICONSTOP "The adjacent Cerebral core payload is missing. Keep the Core folder beside Cerebral-Setup.exe." /SD IDOK
    SetErrorLevel 1603
    Quit
  StrCpy $CortexCheckDirectory ""
  StrCpy $CortexCheckDataDirectory ""
  StrCpy $CortexInstallAttempted 0
  StrCpy $Portable 0
  StrCpy $ProjectsDir ""
  StrCpy $ProjectsSpecified 0
  StrCpy $DesktopShortcuts 1
  StrCpy $ExistingLayout 0
  StrCpy $ComponentsInitialized 0
  StrCpy $CacheInstallationSourceRequested ""
  StrCpy $CortexBehaviorPacksRequested ""
  StrCpy $BundledPluginsRequested ""
  StrCpy $SkipCortexInstallRequested "0"
  StrCpy $EarlyGateDiagnosticsStarted 0
  StrCpy $InstallationSourceCache ""
  # NSIS is a 32-bit bootstrap. Use Sysnative so discovery, registry and
  # Program Files probes observe the installed 64-bit workstation state.
  StrCpy $NativePowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$NativePowerShell" native_powershell_ready 0
    StrCpy $NativePowerShell "$SYSDIR\WindowsPowerShell\v1.0\powershell.exe"
  native_powershell_ready:
  ${GetParameters} $Args
  ClearErrors
  ${GetOptions} $Args "/PORTABLE" $0
  ${IfNot} ${Errors}
    StrCpy $Portable 1
    ${If} $INSTDIR == ""
      StrCpy $INSTDIR "$LOCALAPPDATA\Pegasus\Cerebral\portable"
    ${EndIf}
    StrCpy $BaseDir "$INSTDIR"
    StrCpy $StateDir "$INSTDIR\Data"
    StrCpy $ModelsDir "$INSTDIR\Models"
    StrCpy $ToolsDir "$INSTDIR\Tools"
    StrCpy $ProjectsDir "$INSTDIR\Projects"
    StrCpy $SharedDir "$INSTDIR\Shared"
    StrCpy $UserDataDir "$INSTDIR\UserData"
    ${If} ${FileExists} "$INSTDIR\installation.ini"
      StrCpy $ExistingLayout 1
    ${EndIf}
  ${Else}
    !ifndef CEREBRAL_UI_TEST
      UserInfo::GetAccountType
      Pop $0
      ${If} $0 != "admin"
        MessageBox MB_ICONSTOP "Service installation requires administrator rights. Run this installer as administrator, or use Integrator's Install/Update action. /PORTABLE installs files only without administrator rights." /SD IDOK
        SetErrorLevel 740
        Quit
      ${EndIf}
    !endif
    SetShellVarContext all
  ${EndIf}
  ${If} $Portable == 0
    ReadRegStr $2 HKLM "Software\Pegasus\CerebralWorker" "InstallDir"
    ${If} $2 != ""
      MessageBox MB_ICONSTOP "A standalone Pegasus Cerebral Worker is already installed at $2. Uninstall the standalone Worker before installing the full x64 Cerebral product so the shared PegasusCerebral service has one owner." /SD IDOK
      SetErrorLevel 1603
      Quit
    ${EndIf}
    ReadRegStr $1 HKLM "Software\Pegasus\Cerebral" "InstallDir"
    ${If} $1 != ""
      StrCpy $ExistingLayout 1
      StrCpy $INSTDIR "$1"
      ReadRegStr $BaseDir HKLM "Software\Pegasus\Cerebral" "BaseDir"
      ${If} $BaseDir == ""
        ${GetParent} "$INSTDIR" $BaseDir
      ${EndIf}
      ReadRegStr $StateDir HKLM "Software\Pegasus\Cerebral" "DataDir"
      ReadRegStr $ProjectsDir HKLM "Software\Pegasus\Cerebral" "ProjectsDir"
      ReadRegStr $ModelsDir HKLM "Software\Pegasus\Cerebral" "ModelsDir"
      ReadRegStr $ToolsDir HKLM "Software\Pegasus\Cerebral" "ToolsDir"
      ReadRegStr $SharedDir HKLM "Software\Pegasus\Cerebral" "SharedDir"
      ${If} $ModelsDir == ""
        StrCpy $ModelsDir "$StateDir\models"
      ${EndIf}
      ${If} $SharedDir == ""
        StrCpy $SharedDir "$StateDir\Shared"
      ${EndIf}
      ${If} $ToolsDir == ""
        StrCpy $ToolsDir "$StateDir\Tools"
      ${EndIf}
      ReadRegStr $UserDataDir HKLM "Software\Pegasus\Cerebral" "UserDataDir"
      ${If} $UserDataDir == ""
        StrCpy $UserDataDir "$LOCALAPPDATA\Pegasus\Cerebral"
      ${EndIf}
      ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "DesktopShortcuts"
      ${If} $0 == 0
        StrCpy $DesktopShortcuts 0
      ${EndIf}
    ${Else}
      ${If} $INSTDIR == ""
        StrCpy $INSTDIR "$PROGRAMFILES64\Pegasus\Cerebral"
      ${EndIf}
      # SetShellVarContext all makes $APPDATA the machine-wide ProgramData root.
      StrCpy $BaseDir "$APPDATA\Pegasus\Cerebral"
      StrCpy $StateDir "$BaseDir"
      StrCpy $ModelsDir "$BaseDir\Models"
      StrCpy $ToolsDir "$BaseDir\Tools"
      # Projects are a distinct agent workspace, parallel to the familiar
      # C:\CodexProjects layout and independent of replaceable program/data files.
      # NSIS has no $SYSDRIVE variable. Derive the local Windows drive root
      # from $WINDIR so the default remains portable across drive letters.
      StrCpy $ProjectsDir "$WINDIR" 3
      StrCpy $ProjectsDir "$ProjectsDir\CerebralProjects"
      StrCpy $SharedDir "$BaseDir\Shared"
      StrCpy $UserDataDir "$BaseDir\Users"
    ${EndIf}
    ClearErrors
    ${GetOptions} $Args "/DATA=" $0
    ${IfNot} ${Errors}
      ${If} $ExistingLayout == 1
        MessageBox MB_ICONSTOP "Changing the writable data root during an in-place update is not supported. Uninstall Cerebral first or omit /DATA to preserve the registered data root." /SD IDOK
        SetErrorLevel 1603
        Quit
      ${EndIf}
      ${If} $0 == ""
        MessageBox MB_ICONSTOP "/DATA requires an absolute product-specific writable data folder." /SD IDOK
        SetErrorLevel 1603
        Quit
      ${EndIf}
      StrCpy $BaseDir "$0"
      StrCpy $StateDir "$BaseDir"
      StrCpy $ModelsDir "$BaseDir\Models"
      StrCpy $ToolsDir "$BaseDir\Tools"
      StrCpy $SharedDir "$BaseDir\Shared"
      StrCpy $UserDataDir "$BaseDir\Users"
    ${EndIf}
    ClearErrors
    ${GetOptions} $Args "/PROJECTS=" $ProjectsDir
    ${IfNot} ${Errors}
      StrCpy $ProjectsSpecified 1
      ${If} $ProjectsDir == ""
        MessageBox MB_ICONSTOP "/PROJECTS requires an absolute dedicated project folder." /SD IDOK
        SetErrorLevel 1603
        Quit
      ${EndIf}
    ${EndIf}
    ClearErrors
    ${GetOptions} $Args "/DESKTOP" $0
    ${IfNot} ${Errors}
      StrCpy $DesktopShortcuts 1
    ${EndIf}
  ${EndIf}
  Call DiscoverModelPackages
  Call LoadInstallerState
  # Component initialization is setup state, not presentation. Silent
  # upgrade/repair runs must preserve prior choices just like the visible page.
  Call PrepareComponentsPage
FunctionEnd

SectionGroup /e "Core platform (required)" CorePlatformGroup
Section "Pegasus Cerebral application" SecCoreApplication
  SectionIn RO
  AddSize 1
SectionEnd
Section "Pegasus Cortex continuity service" SecPrereqCortex
  SectionIn RO
  AddSize 200000
SectionEnd
Section "Microsoft Visual C++ x64 runtime" SecPrereqVcRedist
  SectionIn RO
  AddSize 50000
SectionEnd
Section "Windows trust certificate" SecCortexTrust
  AddSize 10
SectionEnd
SectionGroupEnd

SectionGroup /e "Cerebral capabilities" PluginGroup
Section "AI Dictation (Parakeet, CPU)" SecPluginAiDictation
  AddSize 720000
SectionEnd
Section "Books" SecPluginBooks
  AddSize 700
SectionEnd
Section "Browser" SecPluginBrowser
  AddSize 150000
SectionEnd
Section "Programming" SecPluginProgramming
  AddSize 200
SectionEnd
Section "Prompt Review" SecPluginPromptReview
  AddSize 90
SectionEnd
Section "Windows Dictation" SecPluginDictation
  AddSize 2200
SectionEnd
SectionGroupEnd

SectionGroup /e "Cortex knowledge" CortexBehaviorPackGroup
Section "Cerebral agent guides" SecCortexBehaviorPackCerebralGuides
  AddSize 50
SectionEnd
Section "Windows operations guide" SecCortexBehaviorPackWindowsKnowledge
  AddSize 50
SectionEnd
SectionGroupEnd

SectionGroup /e "Developer workstation tools" OptionalDeveloperGroup
Section ".NET 10 SDK and desktop runtime" SecPrereqDotnet
  AddSize 1200000
SectionEnd
Section "PowerShell 7" SecPrereqPowerShell
  AddSize 400000
SectionEnd
Section "Git for Windows" SecPrereqGit
  AddSize 400000
SectionEnd
Section "Node.js LTS" SecPrereqNode
  AddSize 250000
SectionEnd
Section "Python" SecPrereqPython
  AddSize 200000
SectionEnd
Section "7-Zip" SecPrereqSevenZip
  AddSize 10000
SectionEnd
Section "NSIS installer builder" SecPrereqNsis
  AddSize 50000
SectionEnd
Section "Visual Studio Build Tools" SecPrereqVsBuildTools
  AddSize 10000000
SectionEnd
Section "Windows Subsystem for Linux" SecPrereqWsl
  AddSize 1000000
SectionEnd
SectionGroupEnd

SectionGroup /e "Codex integration" CortexPrerequisiteGroup
Section /o "Register Cortex MCP and PAO tools with Codex" SecCortexCodexRegistration
  AddSize 10
SectionEnd
Section /o "Add Cortex guidance to Codex AGENTS.md" SecCortexCodexGuidance
  AddSize 10
SectionEnd
SectionGroupEnd

SectionGroup /e "Recovery" InstallationSupportGroup
Section /o "Keep offline installation source for repair or reinstall" SecCacheInstallationSource
  AddSize 12500000
SectionEnd
SectionGroupEnd

!macro ApplyComponentMetadata SectionId ComponentId DisplayName
  ReadINIStr $0 "$EXEDIR\kit.ini" "component.${ComponentId}" "sizeKib"
  ${If} $0 != ""
    SectionSetSize ${SectionId} $0
  ${EndIf}
  SectionSetText ${SectionId} "${DisplayName}"
  ${If} $InstallerStateFile != ""
    ReadINIStr $0 "$InstallerStateFile" "componentStates" "${ComponentId}.state"
    ReadINIStr $2 "$InstallerStateFile" "componentStates" "${ComponentId}.installedVersion"
    ReadINIStr $3 "$InstallerStateFile" "componentStates" "${ComponentId}.packagedVersion"
    ${If} $0 != ""
    ${AndIf} $0 != "not-installed"
      SectionGetFlags ${SectionId} $1
      IntOp $1 $1 | ${SF_SELECTED}
      IntOp $1 $1 | ${SF_RO}
      SectionSetFlags ${SectionId} $1
      ${If} $0 == "current"
        SectionSetText ${SectionId} "${DisplayName} - Current ($2)"
      ${ElseIf} $0 == "update"
        SectionSetText ${SectionId} "${DisplayName} - Update available ($2 to $3)"
      ${ElseIf} $0 == "newer"
        SectionSetText ${SectionId} "${DisplayName} - Installed version is newer ($2)"
      ${Else}
        SectionSetText ${SectionId} "${DisplayName} - Already installed"
      ${EndIf}
    ${EndIf}
  ${EndIf}
!macroend

!macro EnhanceComponentDescription SectionId ComponentId Fallback
  ${If} $0 == ${SectionId}
    ReadINIStr $1 "$EXEDIR\kit.ini" "component.${ComponentId}" "description"
    ${If} $1 == ""
      StrCpy $1 "${Fallback}"
    ${EndIf}
    ReadINIStr $2 "$EXEDIR\kit.ini" "component.${ComponentId}" "version"
    ReadINIStr $3 "$EXEDIR\kit.ini" "component.${ComponentId}" "sizeKib"
    StrCpy $4 "Not installed"
    ${If} $InstallerStateFile != ""
      ReadINIStr $5 "$InstallerStateFile" "componentStates" "${ComponentId}.state"
      ReadINIStr $6 "$InstallerStateFile" "componentStates" "${ComponentId}.installedVersion"
      ${If} $5 == "current"
        StrCpy $4 "Current - installed $6"
      ${ElseIf} $5 == "update"
        StrCpy $4 "Update available - installed $6; package $2"
      ${ElseIf} $5 == "newer"
        StrCpy $4 "Installed version is newer - installed $6; package $2"
      ${ElseIf} $5 == "installed-unknown"
        StrCpy $4 "Already installed - prior version was not recorded"
      ${EndIf}
    ${EndIf}
    ${If} $3 == ""
      StrCpy $7 "Size is calculated from the selected installation source."
    ${ElseIf} $3 < 1024
      StrCpy $7 "Package or installed footprint: $3 KiB."
    ${Else}
      IntOp $8 $3 + 1023
      IntOp $8 $8 / 1024
      StrCpy $7 "Package or installed footprint: approximately $8 MiB."
    ${EndIf}
    SendMessage $mui.ComponentsPage.DescriptionText ${WM_SETTEXT} 0 "STR:$1$\r$\n$\r$\nStatus: $4.$\r$\n$7"
  ${EndIf}
!macroend

!define MUI_CUSTOMFUNCTION_ONMOUSEOVERSECTION EnhanceComponentDescription
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCoreApplication} "Required Pegasus Cerebral desktop application, worker, service host, shared contracts and default configuration. Updates replace only program files."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqCortex} "Required local continuity and AgentOps service used for retained project knowledge and bounded system operations."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqVcRedist} "Required Microsoft Visual C++ x64 runtime used by local model-inference components."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCortexTrust} "Recommended Windows trust certificate for Pegasus-signed binaries and the secure Cerebral-to-Cortex service pipe."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginAiDictation} "Installs the local NVIDIA Parakeet dictation plugin, its CPU runtime and packaged speech model. It does not consume the LLM's GPU allocation."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginBooks} "Installs the Book Bible and durable long-form authoring workflow."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginBrowser} "Installs isolated, bounded local browser control. A compatible Chromium-based browser runtime must be configured separately."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginProgramming} "Installs structured application-planning, editing, build and review workflow support."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginPromptReview} "Installs read-back and prompt-clarification support before a request is submitted."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPluginDictation} "Installs the Windows speech-input integration as an alternate dictation provider."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCortexBehaviorPackCerebralGuides} "Adds the current Cerebral contracts and operating guidance to Cortex for bounded agent retrieval."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCortexBehaviorPackWindowsKnowledge} "Adds curated Windows operations knowledge to Cortex for bounded agent retrieval."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqDotnet} "Required shared .NET desktop runtime plus the SDK used to build and test .NET projects and Cerebral extensions."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqPowerShell} "Installs PowerShell 7 for registered scripts, automation and developer workflows."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqGit} "Installs Git for Windows for source control, snapshots and project history."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqNode} "Installs the Node.js long-term-support runtime and package tools."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqPython} "Installs Python for local tools, model helpers and plugin development."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqSevenZip} "Installs 7-Zip for supported archive inspection and extraction."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqNsis} "Installs the NSIS compiler used to build Windows installers."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqVsBuildTools} "Installs MSVC, MSBuild, CMake, Ninja, vcpkg and the Windows SDK for native and Windows desktop builds."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecPrereqWsl} "Enables Windows Subsystem for Linux for workflows that require a Linux environment. A restart may be required."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCortexCodexRegistration} "Registers the installed Cortex MCP and PAO tools with Codex on this computer."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCortexCodexGuidance} "Adds the managed Cortex guidance block to the user's Codex AGENTS.md file."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCacheInstallationSource} "Copies this installation source to local storage so repair or reinstall does not require the original USB drive or download."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function EnhanceComponentDescription
  !insertmacro EnhanceComponentDescription ${SecCoreApplication} "cerebral-core" "Required Pegasus Cerebral desktop application, worker, service host, shared contracts and default configuration."
  !insertmacro EnhanceComponentDescription ${SecPrereqCortex} "cortex-core" "Required local continuity and AgentOps service used for retained project knowledge and bounded system operations."
  !insertmacro EnhanceComponentDescription ${SecPrereqVcRedist} "vc-redist" "Required Microsoft Visual C++ x64 runtime used by local model-inference components."
  !insertmacro EnhanceComponentDescription ${SecCortexTrust} "cortex-trust" "Recommended Windows trust certificate for Pegasus-signed binaries."
  !insertmacro EnhanceComponentDescription ${SecPluginAiDictation} "pegasus.ai-dictation" "Local NVIDIA Parakeet dictation, CPU runtime and speech model."
  !insertmacro EnhanceComponentDescription ${SecPluginBooks} "pegasus.books" "Book Bible and durable long-form authoring workflow."
  !insertmacro EnhanceComponentDescription ${SecPluginBrowser} "pegasus.browser" "Isolated local Chromium browser control."
  !insertmacro EnhanceComponentDescription ${SecPluginProgramming} "pegasus.programming" "Structured application planning, editing, build and review workflows."
  !insertmacro EnhanceComponentDescription ${SecPluginPromptReview} "pegasus.prompt-review" "Optional prompt refinement before submission."
  !insertmacro EnhanceComponentDescription ${SecPluginDictation} "pegasus.dictation" "Windows speech-input integration as an alternate provider."
  !insertmacro EnhanceComponentDescription ${SecCortexBehaviorPackCerebralGuides} "pegasus-cerebral-guides-v1" "Current Cerebral contracts and operating guidance."
  !insertmacro EnhanceComponentDescription ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge-v1" "Curated Windows operations knowledge."
  !insertmacro EnhanceComponentDescription ${SecPrereqDotnet} "dotnet-sdk" "Required shared .NET desktop runtime and SDK for Cerebral applications and extension builds."
  !insertmacro EnhanceComponentDescription ${SecPrereqPowerShell} "powershell" "PowerShell 7 for registered scripts and automation."
  !insertmacro EnhanceComponentDescription ${SecPrereqGit} "git" "Git for source control and project history."
  !insertmacro EnhanceComponentDescription ${SecPrereqNode} "node" "Node.js LTS runtime and package tools."
  !insertmacro EnhanceComponentDescription ${SecPrereqPython} "python" "Python runtime for tools and model helpers."
  !insertmacro EnhanceComponentDescription ${SecPrereqSevenZip} "sevenzip" "7-Zip archive support."
  !insertmacro EnhanceComponentDescription ${SecPrereqNsis} "nsis" "NSIS Windows installer builder."
  !insertmacro EnhanceComponentDescription ${SecPrereqVsBuildTools} "vs-buildtools" "Visual Studio native and desktop build tools."
  !insertmacro EnhanceComponentDescription ${SecPrereqWsl} "wsl" "Windows Subsystem for Linux."
  !insertmacro EnhanceComponentDescription ${SecCortexCodexRegistration} "cortex-codex-registration" "Register Cortex MCP and PAO tools with Codex."
  !insertmacro EnhanceComponentDescription ${SecCortexCodexGuidance} "cortex-codex-guidance" "Add managed Cortex guidance to Codex."
  !insertmacro EnhanceComponentDescription ${SecCacheInstallationSource} "installation-source-cache" "Keep an offline source for repair or reinstall."
FunctionEnd

# This function follows the section declarations because NSIS creates the section
# identifiers while parsing those declarations.
Function InstallCortexPrerequisite
  # Portable extraction is files-only. It may require and inspect an existing
  # Cortex installation below, but it must never install Cortex, refresh trust,
  # or alter Codex integration on the host.
  ${If} $Portable == 1
    Return
  ${EndIf}
  ${If} $SkipCortexInstallRequested == "1"
    # Leave optional Cortex changes for a separate maintenance operation, while
    # retaining the read-only compatibility gate in the core section.
    DetailPrint "Keeping the registered Cortex installation and saved optional choices for this in-place update."
    Return
  ${EndIf}
  StrCpy $CortexInstallerArguments "/S /TRUST=0"
  StrCpy $3 0
  SectionGetFlags ${SecCortexTrust} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ReadINIStr $4 "$InstallerStateFile" "componentStates" "cortex-trust.state"
  ${If} $0 == ${SF_SELECTED}
  ${AndIf} $4 != "current"
  ${AndIf} $4 != "newer"
    StrCpy $CortexInstallerArguments "/S /TRUST=1"
    StrCpy $3 1
  ${EndIf}
  SectionGetFlags ${SecCortexCodexRegistration} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ReadINIStr $4 "$InstallerStateFile" "componentStates" "cortex-codex-registration.state"
  ${If} $0 == ${SF_SELECTED}
  ${AndIf} $4 != "current"
  ${AndIf} $4 != "newer"
    StrCpy $CortexInstallerArguments "$CortexInstallerArguments /CODEXREGISTER=1"
    StrCpy $3 1
  ${EndIf}
  SectionGetFlags ${SecCortexCodexGuidance} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ReadINIStr $4 "$InstallerStateFile" "componentStates" "cortex-codex-guidance.state"
  ${If} $0 == ${SF_SELECTED}
  ${AndIf} $4 != "current"
  ${AndIf} $4 != "newer"
    StrCpy $CortexInstallerArguments "$CortexInstallerArguments /CODEXGUIDANCE=1"
    StrCpy $3 1
  ${EndIf}
  Call CheckCortexPrerequisite
  ${If} $0 == 0
  ${AndIf} $3 == 0
    DetailPrint "Pegasus Cortex already satisfies Cerebral's minimum release; keeping the current installation."
    Return
  ${EndIf}
  IfFileExists "$EXEDIR\Pegasus-Cortex-Setup\PegasusCortexInstaller.exe" +4 0
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-companion-missing"
    SetErrorLevel 1603
    Abort "The required Pegasus Cortex installer is missing. Keep the complete Pegasus-Cortex-Setup folder beside Cerebral-Setup.exe."
  StrCpy $CortexInstallAttempted 1
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-companion-started"
  DetailPrint "Installing or updating required Pegasus Cortex core..."
  ExecWait '"$EXEDIR\Pegasus-Cortex-Setup\PegasusCortexInstaller.exe" $CortexInstallerArguments' $2
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexCompanionExit" "$2"
  ${If} $2 != 0
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-companion-failed"
    SetErrorLevel 1603
    Abort "Pegasus Cortex silent setup returned $2. Cerebral files have not been replaced. Review the Cortex installer log and retry."
  ${EndIf}
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-companion-complete"
FunctionEnd

!macro ImportCortexBehaviorPack SectionId PackDirectory DisplayName
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    ${IfNot} ${FileExists} "$EXEDIR\BehaviorPacks\${PackDirectory}\behavior-pack.json"
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-missing"
      SetErrorLevel 1603
      Abort "The selected ${DisplayName} module is missing. Keep the complete BehaviorPacks folder beside Cerebral-Setup.exe. Cerebral files have not been replaced."
    ${EndIf}
    ReadRegStr $1 HKLM "Software\Pegasus\Cortex" "InstallLocation"
    ${If} $1 == ""
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-registration-missing"
      SetErrorLevel 1603
      Abort "Pegasus Cortex did not publish its installation location. Repair Cortex, then rerun Cerebral setup. Cerebral files have not been replaced."
    ${EndIf}
    IfFileExists "$1\PegasusCortex.exe" +4 0
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-executable-missing"
      SetErrorLevel 1603
      Abort "The registered Pegasus Cortex executable is missing. Repair Cortex, then rerun Cerebral setup. Cerebral files have not been replaced."
    DetailPrint "Applying ${DisplayName} to Cortex knowledge.shared..."
    nsExec::ExecToStack /TIMEOUT=120000 '"$1\PegasusCortex.exe" import-behavior-pack --pack-root "$EXEDIR\BehaviorPacks\${PackDirectory}" --namespace knowledge.shared'
    Pop $0
    Pop $2
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexBehaviorExit" "$0"
    ${If} $0 != 0
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-failed"
      SetErrorLevel 1603
      Abort "Cortex rejected the selected ${DisplayName} module. Cerebral files have not been replaced. Existing Cortex knowledge remains protected.$\r$\n$\r$\n$2"
    ${EndIf}
  ${EndIf}
!macroend

Function InstallSelectedCortexBehaviorPacks
  ${If} $SkipCortexInstallRequested == "1"
    DetailPrint "Keeping Cortex knowledge modules unchanged for this in-place update."
    Return
  ${EndIf}
  # Each Cortex operation is atomic and owns its database transaction. Unselected
  # modules are left alone; setup never removes retained knowledge during an upgrade.
  !insertmacro ImportCortexBehaviorPack ${SecCortexBehaviorPackCerebralGuides} "cerebral-guides" "Cerebral operating guides"
  !insertmacro ImportCortexBehaviorPack ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge" "Windows operational knowledge"
FunctionEnd

!macro SetPluginInitial SectionId PluginId
  ReadINIStr $0 "$INSTDIR\installation.ini" "Components" "Plugin.${PluginId}"
  SectionGetFlags ${SectionId} $1
  ${If} $0 == "0"
    IntOp $1 $1 & 0xFFFFFFFE
  ${ElseIf} $0 == "1"
    IntOp $1 $1 | ${SF_SELECTED}
  ${ElseIf} $ExistingLayout == 1
    ${If} ${FileExists} "$INSTDIR\Plugins\Desktop\${PluginId}\desktop-plugin.json"
      IntOp $1 $1 | ${SF_SELECTED}
    ${ElseIf} ${FileExists} "$INSTDIR\Plugins\Service\${PluginId}\service-plugin.json"
      IntOp $1 $1 | ${SF_SELECTED}
    ${Else}
      IntOp $1 $1 & 0xFFFFFFFE
    ${EndIf}
  ${EndIf}
  SectionSetFlags ${SectionId} $1
!macroend

!macro SetPrerequisiteInitial SectionId PrerequisiteId
  ReadINIStr $0 "$INSTDIR\installation.ini" "Components" "Prerequisite.${PrerequisiteId}"
  SectionGetFlags ${SectionId} $1
  ${If} $0 == "0"
    IntOp $1 $1 & 0xFFFFFFFE
  ${ElseIf} $0 == "1"
    IntOp $1 $1 | ${SF_SELECTED}
  ${EndIf}
  SectionSetFlags ${SectionId} $1
!macroend

!macro SetBehaviorPackInitial SectionId PackId
  ReadINIStr $0 "$INSTDIR\installation.ini" "Components" "BehaviorPack.${PackId}"
  SectionGetFlags ${SectionId} $1
  ${If} $0 == "0"
    IntOp $1 $1 & 0xFFFFFFFE
  ${ElseIf} $0 == "1"
    IntOp $1 $1 | ${SF_SELECTED}
  ${EndIf}
  SectionSetFlags ${SectionId} $1
!macroend

!macro AppendPrerequisite SectionId PrerequisiteId
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    # An update may retain the saved Python checkbox without materializing a
    # missing optional Python runtime merely because its fallback is bundled.
    ${If} "${PrerequisiteId}" == "python"
    ${AndIf} $SkipPythonInstallRequested == "1"
      Goto skip_optional_python_${SectionId}
    ${EndIf}
    ReadINIStr $1 "$InstallerStateFile" "componentStates" "${PrerequisiteId}.state"
    ${If} $1 != "current"
    ${AndIf} $1 != "newer"
    ${If} $PrerequisiteComponents == ""
      StrCpy $PrerequisiteComponents "${PrerequisiteId}"
    ${Else}
      StrCpy $PrerequisiteComponents "$PrerequisiteComponents,${PrerequisiteId}"
    ${EndIf}
    ${If} $1 == "update"
      ${If} $PrerequisiteForceComponents == ""
        StrCpy $PrerequisiteForceComponents "${PrerequisiteId}"
      ${Else}
        StrCpy $PrerequisiteForceComponents "$PrerequisiteForceComponents,${PrerequisiteId}"
      ${EndIf}
    ${EndIf}
    ${EndIf}
  ${EndIf}
  skip_optional_python_${SectionId}:
!macroend

!macro SavePrerequisiteSelection SectionId PrerequisiteId
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "Prerequisite.${PrerequisiteId}" "1"
  ${Else}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "Prerequisite.${PrerequisiteId}" "0"
  ${EndIf}
!macroend

!macro SaveBehaviorPackSelection SectionId PackId
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "BehaviorPack.${PackId}" "1"
  ${Else}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "BehaviorPack.${PackId}" "0"
  ${EndIf}
!macroend

Function SaveBehaviorPackSelections
  ${If} $SkipCortexInstallRequested == "1"
    Return
  ${EndIf}
  !insertmacro SaveBehaviorPackSelection ${SecCortexBehaviorPackCerebralGuides} "pegasus-cerebral-guides-v1"
  !insertmacro SaveBehaviorPackSelection ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge-v1"
FunctionEnd

Function SavePrerequisiteSelections
  WriteINIStr "$INSTDIR\installation.ini" "Components" "Prerequisite.cortex-core" "1"
  ${If} $SkipCortexInstallRequested != "1"
  !insertmacro SavePrerequisiteSelection ${SecCortexTrust} "cortex-trust"
  !insertmacro SavePrerequisiteSelection ${SecCortexCodexRegistration} "cortex-codex-registration"
  !insertmacro SavePrerequisiteSelection ${SecCortexCodexGuidance} "cortex-codex-guidance"
  ${EndIf}
  WriteINIStr "$INSTDIR\installation.ini" "Components" "Prerequisite.vc-redist" "1"
  !insertmacro SavePrerequisiteSelection ${SecPrereqDotnet} "dotnet-sdk"
  !insertmacro SavePrerequisiteSelection ${SecPrereqPowerShell} "powershell"
  !insertmacro SavePrerequisiteSelection ${SecPrereqGit} "git"
  !insertmacro SavePrerequisiteSelection ${SecPrereqNode} "node"
  !insertmacro SavePrerequisiteSelection ${SecPrereqPython} "python"
  !insertmacro SavePrerequisiteSelection ${SecPrereqSevenZip} "sevenzip"
  !insertmacro SavePrerequisiteSelection ${SecPrereqNsis} "nsis"
  !insertmacro SavePrerequisiteSelection ${SecPrereqVsBuildTools} "vs-buildtools"
  !insertmacro SavePrerequisiteSelection ${SecPrereqWsl} "wsl"
  !insertmacro SavePrerequisiteSelection ${SecCacheInstallationSource} "installation-source-cache"
FunctionEnd

!macro SaveComponentVersion SectionId ComponentId
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    ReadINIStr $1 "$EXEDIR\kit.ini" "component.${ComponentId}" "version"
    ${If} $1 != ""
      WriteINIStr "$INSTDIR\installation.ini" "ComponentVersions" "${ComponentId}" "$1"
    ${EndIf}
  ${EndIf}
!macroend

Function SaveComponentVersions
  WriteINIStr "$INSTDIR\installation.ini" "ComponentVersions" "cerebral-core" "${CEREBRAL_VERSION}"
  ${If} $SkipCortexInstallRequested != "1"
  !insertmacro SaveComponentVersion ${SecPrereqCortex} "cortex-core"
  !insertmacro SaveComponentVersion ${SecCortexTrust} "cortex-trust"
  !insertmacro SaveComponentVersion ${SecCortexBehaviorPackCerebralGuides} "pegasus-cerebral-guides-v1"
  !insertmacro SaveComponentVersion ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge-v1"
  !insertmacro SaveComponentVersion ${SecCortexCodexRegistration} "cortex-codex-registration"
  !insertmacro SaveComponentVersion ${SecCortexCodexGuidance} "cortex-codex-guidance"
  ${EndIf}
  !insertmacro SaveComponentVersion ${SecPrereqVcRedist} "vc-redist"
  !insertmacro SaveComponentVersion ${SecPluginAiDictation} "pegasus.ai-dictation"
  !insertmacro SaveComponentVersion ${SecPluginBooks} "pegasus.books"
  !insertmacro SaveComponentVersion ${SecPluginBrowser} "pegasus.browser"
  !insertmacro SaveComponentVersion ${SecPluginProgramming} "pegasus.programming"
  !insertmacro SaveComponentVersion ${SecPluginPromptReview} "pegasus.prompt-review"
  !insertmacro SaveComponentVersion ${SecPluginDictation} "pegasus.dictation"
  !insertmacro SaveComponentVersion ${SecPrereqDotnet} "dotnet-sdk"
  !insertmacro SaveComponentVersion ${SecPrereqPowerShell} "powershell"
  !insertmacro SaveComponentVersion ${SecPrereqGit} "git"
  !insertmacro SaveComponentVersion ${SecPrereqNode} "node"
  !insertmacro SaveComponentVersion ${SecPrereqPython} "python"
  !insertmacro SaveComponentVersion ${SecPrereqSevenZip} "sevenzip"
  !insertmacro SaveComponentVersion ${SecPrereqNsis} "nsis"
  !insertmacro SaveComponentVersion ${SecPrereqVsBuildTools} "vs-buildtools"
  !insertmacro SaveComponentVersion ${SecPrereqWsl} "wsl"
  !insertmacro SaveComponentVersion ${SecCacheInstallationSource} "installation-source-cache"
FunctionEnd

Function InstallOfflinePrerequisites
  ${If} $Portable == 1
    Return
  ${EndIf}
  StrCpy $PrerequisiteComponents ""
  StrCpy $PrerequisiteForceComponents ""
  !insertmacro AppendPrerequisite ${SecPrereqVcRedist} "vc-redist"
  !insertmacro AppendPrerequisite ${SecPrereqDotnet} "dotnet-sdk"
  !insertmacro AppendPrerequisite ${SecPrereqPowerShell} "powershell"
  !insertmacro AppendPrerequisite ${SecPrereqGit} "git"
  !insertmacro AppendPrerequisite ${SecPrereqNode} "node"
  !insertmacro AppendPrerequisite ${SecPrereqPython} "python"
  !insertmacro AppendPrerequisite ${SecPrereqSevenZip} "sevenzip"
  !insertmacro AppendPrerequisite ${SecPrereqNsis} "nsis"
  !insertmacro AppendPrerequisite ${SecPrereqVsBuildTools} "vs-buildtools"
  !insertmacro AppendPrerequisite ${SecPrereqWsl} "wsl"
  ${If} $PrerequisiteComponents == ""
    Return
  ${EndIf}
  IfFileExists "$EXEDIR\Prerequisites\Install-CerebralPrerequisites.ps1" +4 0
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "offline-prerequisites-missing"
    SetErrorLevel 1603
    Abort "A required or selected prerequisite is missing from the installation kit. Keep the Prerequisites folder beside Cerebral-Setup.exe."
  nsExec::ExecToLog /TIMEOUT=7200000 '"$NativePowerShell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$EXEDIR\Prerequisites\Install-CerebralPrerequisites.ps1" -PackageRoot "$EXEDIR\Prerequisites" -Components "$PrerequisiteComponents" -ForceComponents "$PrerequisiteForceComponents" -ReceiptPath "$StateDir\installation-prerequisites.json"'
  Pop $0
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "OfflinePrerequisitesExit" "$0"
  ${If} $0 != 0
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "offline-prerequisites-failed"
    SetErrorLevel 1603
    Abort "A required or selected prerequisite failed to install or pass its post-install probe. Review the setup log; Cerebral program files have not been replaced."
  ${EndIf}
FunctionEnd

Function InstallAiDictationAssets
  SectionGetFlags ${SecPluginAiDictation} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 != ${SF_SELECTED}
    Return
  ${EndIf}
  IfFileExists "$EXEDIR\Support\Install-CerebralPluginResources.ps1" +3 0
    SetErrorLevel 1603
    Abort "AI Dictation is selected, but the generic plugin-resource installer is missing. Keep the Support folder beside Cerebral-Setup.exe or deselect AI Dictation."
  ClearErrors
  FindFirst $0 $1 "$EXEDIR\Plugins\pegasus.ai-dictation-*.CerebralPlugin"
  ${If} ${Errors}
    SetErrorLevel 1603
    Abort "AI Dictation is selected, but its self-contained .CerebralPlugin package is missing. Keep the Plugins folder beside Cerebral-Setup.exe or deselect AI Dictation."
  ${EndIf}
  FindNext $0 $2
  ${IfNot} ${Errors}
    FindClose $0
    SetErrorLevel 1603
    Abort "More than one AI Dictation package is present. Keep only the current pegasus.ai-dictation package in the Plugins folder."
  ${EndIf}
  FindClose $0
  ExecWait '"$NativePowerShell" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$EXEDIR\Support\Install-CerebralPluginResources.ps1" -PackagePath "$EXEDIR\Plugins\$1" -ToolsDirectory "$ToolsDir" -ModelsDirectory "$ModelsDir" -DataDirectory "$StateDir" -SharedDirectory "$SharedDir" -ReceiptPath "$StateDir\installation-ai-dictation-resources.json"' $0
  ${If} $0 != 0
    SetErrorLevel 1603
    Abort "AI Dictation assets could not be installed or verified. Cerebral program files are present, but the feature was not reported ready."
  ${EndIf}
FunctionEnd

Function CacheInstallationSource
  StrCpy $InstallationSourceCache ""
  ${If} $Portable == 1
    Return
  ${EndIf}
  SectionGetFlags ${SecCacheInstallationSource} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 != ${SF_SELECTED}
    Return
  ${EndIf}
  IfFileExists "$EXEDIR\Support\Cache-CerebralInstallationSource.ps1" +3 0
    SetErrorLevel 1603
    Abort "Offline repair source was selected, but its cache helper is missing. Keep the Support folder beside Cerebral-Setup.exe or deselect the option."
  StrCpy $InstallationSourceCache "$SharedDir\InstallationSource\Current"
  ExecWait '"$NativePowerShell" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$EXEDIR\Support\Cache-CerebralInstallationSource.ps1" -SourceRoot "$EXEDIR" -DestinationRoot "$InstallationSourceCache" -ReceiptPath "$StateDir\installation-source-cache.json"' $0
  ${If} $0 != 0
    SetErrorLevel 1603
    Abort "The offline installation source could not be cached. The installed application and data remain available; correct the destination space or permissions and rerun setup."
  ${EndIf}
FunctionEnd

!macro ApplyPluginSelection SectionId PluginId
  SectionGetFlags ${SectionId} $0
  IntOp $0 $0 & ${SF_SELECTED}
  ${If} $0 == ${SF_SELECTED}
    IfFileExists "$EXEDIR\Support\Install-CerebralFeature.ps1" +3 0
      SetErrorLevel 1603
      Abort "A plugin feature is selected, but the feature installer helper is missing. Keep the Support folder beside Cerebral-Setup.exe."
    IfFileExists "$EXEDIR\Features\${PluginId}.CerebralFeature" +3 0
      SetErrorLevel 1603
      Abort "Selected plugin feature ${PluginId} is missing. Keep the Features folder beside Cerebral-Setup.exe or deselect that feature."
    ExecWait '"$NativePowerShell" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$EXEDIR\Support\Install-CerebralFeature.ps1" -PackagePath "$EXEDIR\Features\${PluginId}.CerebralFeature" -DestinationRoot "$INSTDIR" -ExpectedId "${PluginId}" -ReceiptPath "$StateDir\installation-feature-${PluginId}.json"' $0
    ${If} $0 != 0
      SetErrorLevel 1603
      Abort "Selected plugin feature ${PluginId} could not be published. Its prior generation was preserved when available."
    ${EndIf}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "Plugin.${PluginId}" "1"
  ${Else}
    WriteINIStr "$INSTDIR\installation.ini" "Components" "Plugin.${PluginId}" "0"
    RMDir /r "$INSTDIR\Plugins\Service\${PluginId}"
    RMDir /r "$INSTDIR\Plugins\Desktop\${PluginId}"
  ${EndIf}
!macroend

!macro CreateModelOption Package Size Selected Installed Control Y
  ${If} ${Package} != ""
    IntOp $0 ${Size} / 1024
    ${If} ${Installed} == 1
      ${NSD_CreateCheckbox} 0 ${Y} 100% 14u "${Package} ($0 MB) - Already installed"
    ${Else}
      ${NSD_CreateCheckbox} 0 ${Y} 100% 14u "${Package} ($0 MB)"
    ${EndIf}
    Pop ${Control}
    ${If} ${Selected} == 1
      ${NSD_SetState} ${Control} ${BST_CHECKED}
    ${Else}
      ${NSD_SetState} ${Control} ${BST_UNCHECKED}
    ${EndIf}
    ${If} ${Installed} == 1
      EnableWindow ${Control} 0
    ${EndIf}
    ${NSD_OnClick} ${Control} RefreshModelSpace
  ${EndIf}
!macroend

!macro ReadModelOption Package Selected Control
  ${If} ${Package} != ""
  ${AndIf} ${Control} != ""
    ${NSD_GetState} ${Control} $0
    ${If} $0 == ${BST_CHECKED}
      StrCpy ${Selected} 1
    ${Else}
      StrCpy ${Selected} 0
    ${EndIf}
  ${EndIf}
!macroend

!macro AddSelectedModelSize Package Size Selected Total
  ${If} ${Package} != ""
  ${AndIf} ${Selected} == 1
    IntOp ${Total} ${Total} + ${Size}
  ${EndIf}
!macroend

!macro SaveModelChoice Package Selected
  ${If} ${Package} != ""
    WriteINIStr "$INSTDIR\installation.ini" "Components" "Model.${Package}" "${Selected}"
  ${EndIf}
!macroend

!macro ImportSelectedModel Package Selected Installed
  ${If} ${Package} != ""
  ${AndIf} ${Selected} == 1
  ${AndIf} ${Installed} != 1
    StrCpy $4 "$EXEDIR\Models\${Package}"
    IfFileExists "$4" 0 +2
    Call ImportModelPackage
  ${EndIf}
!macroend

Function DiscoverModelPackages
  StrCpy $ModelPackageCount 0
  StrCpy $ModelPackage0 ""
  StrCpy $ModelPackage1 ""
  StrCpy $ModelPackage2 ""
  StrCpy $ModelPackage3 ""
  StrCpy $ModelPackage4 ""
  StrCpy $ModelPackage5 ""
  StrCpy $ModelPackage6 ""
  StrCpy $ModelPackage7 ""
  StrCpy $ModelSelected0 1
  StrCpy $ModelSelected1 1
  StrCpy $ModelSelected2 1
  StrCpy $ModelSelected3 1
  StrCpy $ModelSelected4 1
  StrCpy $ModelSelected5 1
  StrCpy $ModelSelected6 1
  StrCpy $ModelSelected7 1
  StrCpy $ModelInstalled0 0
  StrCpy $ModelInstalled1 0
  StrCpy $ModelInstalled2 0
  StrCpy $ModelInstalled3 0
  StrCpy $ModelInstalled4 0
  StrCpy $ModelInstalled5 0
  StrCpy $ModelInstalled6 0
  StrCpy $ModelInstalled7 0
  ClearErrors
  FindFirst $ModelFindHandle $ModelFindName "$EXEDIR\Models\*.CerebralLLM"
  ${If} ${Errors}
    Return
  ${EndIf}
  discover_next_model:
  ${If} $ModelFindName == ""
    Goto discover_models_done
  ${EndIf}
  ${GetSize} "$EXEDIR\Models" "/M=$ModelFindName /S=0K /G=0" $0 $1 $2
  ReadINIStr $3 "$INSTDIR\installation.ini" "Components" "Model.$ModelFindName"
  StrCpy $4 0
  ${If} $ExistingLayout == 1
  ${AndIf} $3 == "1"
    StrCpy $4 1
  ${EndIf}
  ${If} $3 != "0"
    StrCpy $3 1
  ${EndIf}
  ${If} $ModelPackageCount == 0
    StrCpy $ModelPackage0 "$ModelFindName"
    StrCpy $ModelSize0 $0
    StrCpy $ModelSelected0 $3
    StrCpy $ModelInstalled0 $4
  ${ElseIf} $ModelPackageCount == 1
    StrCpy $ModelPackage1 "$ModelFindName"
    StrCpy $ModelSize1 $0
    StrCpy $ModelSelected1 $3
    StrCpy $ModelInstalled1 $4
  ${ElseIf} $ModelPackageCount == 2
    StrCpy $ModelPackage2 "$ModelFindName"
    StrCpy $ModelSize2 $0
    StrCpy $ModelSelected2 $3
    StrCpy $ModelInstalled2 $4
  ${ElseIf} $ModelPackageCount == 3
    StrCpy $ModelPackage3 "$ModelFindName"
    StrCpy $ModelSize3 $0
    StrCpy $ModelSelected3 $3
    StrCpy $ModelInstalled3 $4
  ${ElseIf} $ModelPackageCount == 4
    StrCpy $ModelPackage4 "$ModelFindName"
    StrCpy $ModelSize4 $0
    StrCpy $ModelSelected4 $3
    StrCpy $ModelInstalled4 $4
  ${ElseIf} $ModelPackageCount == 5
    StrCpy $ModelPackage5 "$ModelFindName"
    StrCpy $ModelSize5 $0
    StrCpy $ModelSelected5 $3
    StrCpy $ModelInstalled5 $4
  ${ElseIf} $ModelPackageCount == 6
    StrCpy $ModelPackage6 "$ModelFindName"
    StrCpy $ModelSize6 $0
    StrCpy $ModelSelected6 $3
    StrCpy $ModelInstalled6 $4
  ${ElseIf} $ModelPackageCount == 7
    StrCpy $ModelPackage7 "$ModelFindName"
    StrCpy $ModelSize7 $0
    StrCpy $ModelSelected7 $3
    StrCpy $ModelInstalled7 $4
  ${EndIf}
  IntOp $ModelPackageCount $ModelPackageCount + 1
  FindNext $ModelFindHandle $ModelFindName
  Goto discover_next_model
  discover_models_done:
  FindClose $ModelFindHandle
FunctionEnd

Function ModelsPageCreate
  ${If} $Portable == 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Model packages" "Choose the local models to install from the adjacent Models folder."
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${If} $ModelPackageCount == 0
    ${NSD_CreateLabel} 0 0 100% 28u "No .CerebralLLM packages were found beside this installer. A new installation requires at least one model package."
  ${ElseIf} $ModelPackageCount > 8
    ${NSD_CreateLabel} 0 0 100% 28u "$ModelPackageCount packages were found. This installer can select the first 8; import the remaining packages later from Configuration."
  ${Else}
    ${NSD_CreateLabel} 0 0 100% 24u "Models remain separate from the installer. Selected packages are imported from the installation kit and retain their source, license, and package metadata."
  ${EndIf}
  Pop $ModelSummaryLabel
  StrCpy $ModelCheckbox0 ""
  StrCpy $ModelCheckbox1 ""
  StrCpy $ModelCheckbox2 ""
  StrCpy $ModelCheckbox3 ""
  StrCpy $ModelCheckbox4 ""
  StrCpy $ModelCheckbox5 ""
  StrCpy $ModelCheckbox6 ""
  StrCpy $ModelCheckbox7 ""
  !insertmacro CreateModelOption $ModelPackage0 $ModelSize0 $ModelSelected0 $ModelInstalled0 $ModelCheckbox0 28u
  !insertmacro CreateModelOption $ModelPackage1 $ModelSize1 $ModelSelected1 $ModelInstalled1 $ModelCheckbox1 45u
  !insertmacro CreateModelOption $ModelPackage2 $ModelSize2 $ModelSelected2 $ModelInstalled2 $ModelCheckbox2 62u
  !insertmacro CreateModelOption $ModelPackage3 $ModelSize3 $ModelSelected3 $ModelInstalled3 $ModelCheckbox3 79u
  !insertmacro CreateModelOption $ModelPackage4 $ModelSize4 $ModelSelected4 $ModelInstalled4 $ModelCheckbox4 96u
  !insertmacro CreateModelOption $ModelPackage5 $ModelSize5 $ModelSelected5 $ModelInstalled5 $ModelCheckbox5 113u
  !insertmacro CreateModelOption $ModelPackage6 $ModelSize6 $ModelSelected6 $ModelInstalled6 $ModelCheckbox6 130u
  !insertmacro CreateModelOption $ModelPackage7 $ModelSize7 $ModelSelected7 $ModelInstalled7 $ModelCheckbox7 147u
  ${NSD_CreateLabel} 0 166u 100% 22u ""
  Pop $ModelSpaceLabel
  Call RefreshModelSpace
  StrCpy $InstallerLastClientWidth -1
  Call LayoutInstallerWindow
  nsDialogs::Show
FunctionEnd

Function ModelsPageLeave
  Call RefreshModelSpace
  StrCpy $0 0
  ${If} $ModelPackage0 != ""
  ${AndIf} $ModelSelected0 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage1 != ""
  ${AndIf} $ModelSelected1 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage2 != ""
  ${AndIf} $ModelSelected2 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage3 != ""
  ${AndIf} $ModelSelected3 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage4 != ""
  ${AndIf} $ModelSelected4 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage5 != ""
  ${AndIf} $ModelSelected5 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage6 != ""
  ${AndIf} $ModelSelected6 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ModelPackage7 != ""
  ${AndIf} $ModelSelected7 == 1
    IntOp $0 $0 + 1
  ${EndIf}
  ${If} $ExistingLayout == 0
  ${AndIf} $0 == 0
    MessageBox MB_ICONEXCLAMATION "Select at least one model package for a new Pegasus Cerebral installation."
    Abort
  ${EndIf}
  !ifndef CEREBRAL_SKIP_CODEX_PREFLIGHT
    Call ConfirmCodexUnavailable
  !endif
FunctionEnd

!ifndef CEREBRAL_SKIP_CODEX_PREFLIGHT
Function DisableCodexIntegration
  SectionGetFlags ${SecCortexCodexRegistration} $0
  IntOp $0 $0 & 0xFFFFFFFE
  SectionSetFlags ${SecCortexCodexRegistration} $0
  SectionGetFlags ${SecCortexCodexGuidance} $0
  IntOp $0 $0 & 0xFFFFFFFE
  SectionSetFlags ${SecCortexCodexGuidance} $0
FunctionEnd

Function ConfirmCodexUnavailable
  ${If} $SkipCortexInstallRequested == "1"
    Return
  ${EndIf}
  SectionGetFlags ${SecCortexCodexRegistration} $0
  IntOp $0 $0 & ${SF_SELECTED}
  SectionGetFlags ${SecCortexCodexGuidance} $1
  IntOp $1 $1 & ${SF_SELECTED}
  ${If} $0 != ${SF_SELECTED}
  ${AndIf} $1 != ${SF_SELECTED}
    Return
  ${EndIf}
  codex_preflight_retry:
  nsExec::ExecToStack /TIMEOUT=10000 '"$SYSDIR\tasklist.exe" /FI "IMAGENAME eq codex.exe" /NH /FO CSV'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION|MB_RETRYCANCEL "Setup could not verify whether Codex is running.$\r$\n$\r$\nRetry the check, or Cancel to choose whether to skip Codex integration." IDRETRY codex_preflight_retry
    Goto codex_preflight_skip_prompt
  ${EndIf}
  ${StrStr} $2 "$1" "codex.exe"
  ${If} $2 == ""
    ${StrStr} $2 "$1" "Codex.exe"
  ${EndIf}
  ${If} $2 == ""
    Return
  ${EndIf}
  MessageBox MB_ICONEXCLAMATION|MB_RETRYCANCEL "Codex is running. Close Codex before setup changes its optional MCP registration or AGENTS.md guidance.$\r$\n$\r$\nRetry after closing Codex, or Cancel to choose whether to skip those two options." IDRETRY codex_preflight_retry
  codex_preflight_skip_prompt:
  MessageBox MB_ICONQUESTION|MB_YESNO|MB_DEFBUTTON1 "Skip Codex integration and continue installing Pegasus Cerebral?$\r$\n$\r$\nYes: deselect Codex registration and guidance, then continue.$\r$\nNo: cancel setup without applying changes." IDYES codex_preflight_skip
  Quit
  codex_preflight_skip:
  Call DisableCodexIntegration
FunctionEnd
!endif

Function ApplyBundledPluginSelection
  # Component-page state can be normalized by NSIS after .onInit, especially in
  # silent mode. Reapply an explicit unattended Add request at the mutation
  # boundary so it cannot be lost before feature publication.
  ${If} $BundledPluginsRequested == "all"
    SectionGetFlags ${SecPluginAiDictation} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginAiDictation} $0
    SectionGetFlags ${SecPluginBooks} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginBooks} $0
    SectionGetFlags ${SecPluginBrowser} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginBrowser} $0
    SectionGetFlags ${SecPluginProgramming} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginProgramming} $0
    SectionGetFlags ${SecPluginPromptReview} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginPromptReview} $0
    SectionGetFlags ${SecPluginDictation} $0
    IntOp $0 $0 | ${SF_SELECTED}
    SectionSetFlags ${SecPluginDictation} $0
  ${EndIf}
  !insertmacro ApplyPluginSelection ${SecPluginAiDictation} "pegasus.ai-dictation"
  !insertmacro ApplyPluginSelection ${SecPluginBooks} "pegasus.books"
  !insertmacro ApplyPluginSelection ${SecPluginDictation} "pegasus.dictation"
  !insertmacro ApplyPluginSelection ${SecPluginProgramming} "pegasus.programming"
  !insertmacro ApplyPluginSelection ${SecPluginPromptReview} "pegasus.prompt-review"
  !insertmacro ApplyPluginSelection ${SecPluginBrowser} "pegasus.browser"
FunctionEnd

Section "-Pegasus Cerebral core" Main
  SectionIn RO
  StrCpy $EarlyGateDiagnosticsStarted 1
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "starting"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "SkipCortexInstall" "$SkipCortexInstallRequested"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexCompanionExit" "not-started"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexCheckExit" "not-started"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "CortexBehaviorExit" "not-started"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "ProjectPathExit" "not-started"
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "OfflinePrerequisitesExit" "not-started"
  WriteINIStr "$StateDir\installation-stage.ini" "Requested" "BundledPlugins" "$BundledPluginsRequested"
  WriteINIStr "$StateDir\installation-stage.ini" "Requested" "CacheInstallationSource" "$CacheInstallationSourceRequested"
  WriteINIStr "$StateDir\installation-stage.ini" "Requested" "CortexBehaviorPacks" "$CortexBehaviorPacksRequested"
  # Application data is never part of the replaceable installation payload.
  ${If} $INSTDIR == ""
    Abort "Select a product installation directory."
  ${EndIf}
  ${GetRoot} "$INSTDIR" $0
  ${If} $INSTDIR == $0
    Abort "Select a product installation directory, not a drive root."
  ${EndIf}
  # Required prerequisites are installed silently from paths relative to this installer.
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-preparation-started"
  Call InstallCortexPrerequisite
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-preparation-complete"
  # Recheck the final selected paths before stopping services or replacing payload.
  StrCpy $CortexCheckDirectory "$INSTDIR"
  StrCpy $CortexCheckDataDirectory ""
  ${If} $Portable == 0
    StrCpy $CortexCheckDataDirectory "$StateDir"
  ${EndIf}
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-check-started"
  Call RequireCortexPrerequisite
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-check-complete"
  # Admit selected independent knowledge modules through Cortex's public product
  # operation before stopping the Cerebral worker or replacing Cerebral files.
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-started"
  Call InstallSelectedCortexBehaviorPacks
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "cortex-behavior-complete"
  ${If} $Portable == 0
    # Includes silent /PROJECTS= handling. Refresh an unchanged default before stopping the worker.
    ${If} $ProjectsSpecified == 0
      StrCpy $ProjectsDir ""
    ${EndIf}
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "project-path-started"
    Call ResolveProjectPath
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "project-path-complete"
  ${EndIf}
  IfFileExists "$INSTDIR\installation.ini" check_install
  IfFileExists "$INSTDIR\Worker\*" occupied_install
  IfFileExists "$INSTDIR\Integrator\*" occupied_install
  IfFileExists "$INSTDIR\Orchestrator\*" occupied_install
  IfFileExists "$INSTDIR\Applications\*" occupied_install
  IfFileExists "$INSTDIR\Cerebral.Worker.exe" occupied_install known_install
  occupied_install:
    SetErrorLevel 1
    Abort "The selected directory contains unrecognized application folders. Choose an empty product directory."
  check_install:
  ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "Product"
  ${If} $0 != "PegasusCerebral"
    Goto occupied_install
  ${EndIf}
  ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "Portable"
  ${If} $0 != $Portable
    SetErrorLevel 1
    Abort "Use the same portable/service installation mode when updating this directory."
  ${EndIf}
  known_install:
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "offline-prerequisites-started"
  Call InstallOfflinePrerequisites
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "offline-prerequisites-complete"
  ${If} $Portable == 0
    IfFileExists "$INSTDIR\Applications\Worker\Cerebral.Worker.exe" grouped_worker_present flat_worker_check
    flat_worker_check:
    IfFileExists "$INSTDIR\Cerebral.Worker.exe" current_worker_present legacy_worker_check
    legacy_worker_check:
    IfFileExists "$INSTDIR\Worker\Cerebral.Worker.exe" 0 stopped
    nsExec::ExecToLog '"$INSTDIR\Worker\Cerebral.Worker.exe" manage stop'
    Goto worker_stopped_result
    current_worker_present:
    nsExec::ExecToLog '"$INSTDIR\Cerebral.Worker.exe" manage stop'
    Goto worker_stopped_result
    grouped_worker_present:
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage stop'
    worker_stopped_result:
    Pop $0
    ${If} $0 != 0
      MessageBox MB_ICONSTOP "The existing worker could not stop. Close active applications and retry. No files have been replaced." /SD IDOK
      SetErrorLevel 1
      Abort
    ${EndIf}
  ${EndIf}
  stopped:
  CreateDirectory "$INSTDIR"
  WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "Portable" "$Portable"
  WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "Product" "PegasusCerebral"
  Call SavePrerequisiteSelections
  Call SaveBehaviorPackSelections
  ${If} $Portable == 1
  ${OrIf} $ExistingLayout == 0
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "BaseDir" "$BaseDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "DataDir" "$StateDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ModelsDir" "$ModelsDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ToolsDir" "$ToolsDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ProjectsDir" "$ProjectsDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "SharedDir" "$SharedDir"
    WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "UserDataDir" "$UserDataDir"
  ${Else}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "BaseDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "BaseDir" "$BaseDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "DataDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "DataDir" "$StateDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "ModelsDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ModelsDir" "$ModelsDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "ToolsDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ToolsDir" "$ToolsDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "ProjectsDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "ProjectsDir" "$ProjectsDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "SharedDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "SharedDir" "$SharedDir"
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "UserDataDir"
    ${If} $0 == ""
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "UserDataDir" "$UserDataDir"
    ${EndIf}
  ${EndIf}
  # Publish only the adjacent uncompressed core payload. The helper stages all three
  # applications and restores their prior generations if any publish step fails.
  IfFileExists "$EXEDIR\Support\Install-CerebralCore.ps1" +3 0
    SetErrorLevel 1603
    Abort "The Cerebral core installer helper is missing. Keep the Support folder beside Cerebral-Setup.exe."
  nsExec::ExecToLog /TIMEOUT=1800000 '"$NativePowerShell" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$EXEDIR\Support\Install-CerebralCore.ps1" -SourceRoot "$CorePayloadDirectory" -DestinationRoot "$INSTDIR" -ReceiptPath "$StateDir\installation-core.json"'
  Pop $0
  ${If} $0 == 2
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "core-published-finalization-incomplete"
    SetErrorLevel 1603
    Abort "The new core files were published, but cleanup or the completion receipt failed. Keep the installer log and any recovery files. Verify the installed generation before running Setup again."
  ${ElseIf} $0 != 0
    SetErrorLevel 1
    Abort "Core file replacement could not be confirmed. Keep the installer log and any recovery files. Verify the installed generation before running Setup again."
  ${EndIf}
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "core-published"
  Call ApplyBundledPluginSelection
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "features-published"
  Call InstallAiDictationAssets
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "plugin-resources-published"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "Portable" "$Portable"
  WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "Version" "${CEREBRAL_VERSION}"
  ${If} $Portable == 1
    CreateDirectory "$StateDir"
    CreateDirectory "$ModelsDir"
    CreateDirectory "$ToolsDir"
    CreateDirectory "$SharedDir"
    CreateDirectory "$UserDataDir"
  ${Else}
    CreateDirectory "$StateDir"
    ${If} $ExistingLayout == 0
      nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage prepare-user-data --directory "$UserDataDir"'
      Pop $0
      ${If} $0 != 0
        SetErrorLevel 1
        Abort "The per-user profile and plugin location could not be prepared. Program files are present and service data is unchanged; correct the folder permissions and rerun setup."
      ${EndIf}
    ${EndIf}
    # Publish the fully resolved installation roots on both install and update.
    # This repairs legacy records that used display labels or relative paths
    # while preserving custom locations already recovered from registration.
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage set-project-path --data "$StateDir" --directory "$INSTDIR" --projects "$ProjectsDir" --models "$ModelsDir" --tools "$ToolsDir" --shared "$SharedDir"'
    Pop $0
    ${If} $0 != 0
      SetErrorLevel 1
      Abort "The project location could not be saved. The service remains stopped. Inspect the error above, then rerun setup; project files are preserved."
    ${EndIf}
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "paths-configured"
    # Read the value actually preserved or saved before publishing its installation metadata.
    StrCpy $ProjectsDir ""
    Call ResolveProjectPath
    IfFileExists "$EXEDIR\Support\Register-CerebralOwnerPipe.ps1" +3 0
      SetErrorLevel 1
      Abort "The protected owner-pipe installer helper is missing. Keep the complete Support folder beside Cerebral-Setup.exe."
    # The helper retains the original registration child through terminal outcome.
    # Do not let an outer inactivity timeout release it while ACL/SCM/binding work is live.
    nsExec::ExecToLog '"$NativePowerShell" -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$EXEDIR\Support\Register-CerebralOwnerPipe.ps1" -WorkerPath "$INSTDIR\Applications\Worker\Cerebral.Worker.exe" -DataDirectory "$StateDir" -UserDataRoot "$UserDataDir" -ReceiptPath "$StateDir\installation-owner-pipe.json"'
    Pop $0
    ${If} $0 != 0
      SetErrorLevel 1
      Abort "Protected owner-pipe registration failed. The service remains stopped; review the setup details and retry. Program files and data remain available."
    ${EndIf}
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "owner-pipe-registered"
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage start'
    Pop $0
    ${If} $0 != 0
      SetErrorLevel 1
      Abort "The worker did not start. Inspect Worker manage status and the Windows service event log, then retry."
    ${EndIf}
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "service-started"
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage verify --data "$StateDir" --expected-version "${CEREBRAL_VERSION}"'
    Pop $0
    ${If} $0 != 0
      SetErrorLevel 1
      Abort "The worker started but its health/version check failed. Inspect the configured address and worker diagnostics, then retry."
    ${EndIf}
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "service-verified"
    ${If} $PreserveWorkerModels == "1"
      WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "worker-models-preserved"
    ${Else}
      Call ProvisionWorkerModels
    ${EndIf}
    Call CacheInstallationSource
    WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "source-cached"
    WriteRegStr HKLM "Software\Pegasus\Cerebral" "BaseDir" "$BaseDir"
    WriteRegStr HKLM "Software\Pegasus\Cerebral" "InstallDir" "$INSTDIR"
    WriteRegStr HKLM "Software\Pegasus\Cerebral" "DataDir" "$StateDir"
    WriteRegStr HKLM "Software\Pegasus\Cerebral" "ProjectsDir" "$ProjectsDir"
    ReadRegStr $0 HKLM "Software\Pegasus\Cerebral" "ModelsDir"
    ${If} $0 == ""
      WriteRegStr HKLM "Software\Pegasus\Cerebral" "ModelsDir" "$ModelsDir"
    ${EndIf}
    ReadRegStr $0 HKLM "Software\Pegasus\Cerebral" "ToolsDir"
    ${If} $0 == ""
      WriteRegStr HKLM "Software\Pegasus\Cerebral" "ToolsDir" "$ToolsDir"
    ${EndIf}
    ReadRegStr $0 HKLM "Software\Pegasus\Cerebral" "SharedDir"
    ${If} $0 == ""
      WriteRegStr HKLM "Software\Pegasus\Cerebral" "SharedDir" "$SharedDir"
    ${EndIf}
    ReadRegStr $0 HKLM "Software\Pegasus\Cerebral" "UserDataDir"
    ${If} $0 == ""
      WriteRegStr HKLM "Software\Pegasus\Cerebral" "UserDataDir" "$UserDataDir"
    ${EndIf}
    WriteRegStr HKLM "Software\Pegasus\Cerebral" "Version" "${CEREBRAL_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "DisplayName" "Pegasus Cerebral"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "DisplayVersion" "${CEREBRAL_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "Publisher" "Pegasus"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    ${If} $InstallationSourceCache != ""
      WriteRegStr HKLM "Software\Pegasus\Cerebral" "InstallerSource" "$InstallationSourceCache"
      WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "InstallSource" "$InstallationSourceCache"
      WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "ModifyPath" '$\"$InstallationSourceCache\Cerebral-Setup.exe$\"'
      WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "NoModify" 0
      WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "NoRepair" 0
    ${Else}
      DeleteRegValue HKLM "Software\Pegasus\Cerebral" "InstallerSource"
      DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "InstallSource"
      DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "ModifyPath"
      WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "NoModify" 1
      WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral" "NoRepair" 1
    ${EndIf}
    CreateDirectory "$SMPROGRAMS\Pegasus Cerebral"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Integrator.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Orchestrator.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral Configuration.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral.lnk"
    CreateShortcut "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral Configuration.lnk" "$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" "" "$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" 0
    CreateShortcut "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral.lnk" "$INSTDIR\Applications\Desktop\Cerebral.Orchestrator.exe" "" "$INSTDIR\Applications\Desktop\Cerebral.Orchestrator.exe" 0
    WriteRegStr HKLM "Software\Classes\.CerebralPlugin" "" "Pegasus.Cerebral.PluginPackage"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.PluginPackage" "" "Pegasus Cerebral plugin package"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.PluginPackage\DefaultIcon" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe",0'
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.PluginPackage\shell\open\command" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" --import-plugin "%1"'
    WriteRegStr HKLM "Software\Classes\.CerebralLLM" "" "Pegasus.Cerebral.ModelPackage"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.ModelPackage" "" "Pegasus Cerebral model package"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.ModelPackage\DefaultIcon" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe",0'
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.ModelPackage\shell\open\command" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" --import-model "%1"'
    WriteRegStr HKLM "Software\Classes\.CerebralWorker" "" "Pegasus.Cerebral.WorkerInvitation"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.WorkerInvitation" "" "Pegasus Cerebral Worker invitation"
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.WorkerInvitation\DefaultIcon" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe",0'
    WriteRegStr HKLM "Software\Classes\Pegasus.Cerebral.WorkerInvitation\shell\open\command" "" '"$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" --import-worker "%1"'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PegasusCerebralConfiguration"
    ${If} $DesktopShortcuts == 1
      Delete "$DESKTOP\Pegasus Cerebral Configuration.lnk"
      Delete "$DESKTOP\Pegasus Cerebral.lnk"
      CreateShortcut "$DESKTOP\Pegasus Cerebral Configuration.lnk" "$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" "" "$INSTDIR\Applications\Desktop\Cerebral.Integrator.exe" 0
      CreateShortcut "$DESKTOP\Pegasus Cerebral.lnk" "$INSTDIR\Applications\Desktop\Cerebral.Orchestrator.exe" "" "$INSTDIR\Applications\Desktop\Cerebral.Orchestrator.exe" 0
      WriteINIStr "$INSTDIR\installation.ini" "Cerebral" "DesktopShortcuts" "1"
    ${EndIf}
    System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  ${EndIf}
  Call SaveComponentVersions
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "complete"
  SetErrorLevel 0
SectionEnd

Function ProvisionWorkerModels
  # External model packages remain independently replaceable and selectable.
  !insertmacro SaveModelChoice $ModelPackage0 $ModelSelected0
  !insertmacro SaveModelChoice $ModelPackage1 $ModelSelected1
  !insertmacro SaveModelChoice $ModelPackage2 $ModelSelected2
  !insertmacro SaveModelChoice $ModelPackage3 $ModelSelected3
  !insertmacro SaveModelChoice $ModelPackage4 $ModelSelected4
  !insertmacro SaveModelChoice $ModelPackage5 $ModelSelected5
  !insertmacro SaveModelChoice $ModelPackage6 $ModelSelected6
  !insertmacro SaveModelChoice $ModelPackage7 $ModelSelected7
  !insertmacro ImportSelectedModel $ModelPackage0 $ModelSelected0 $ModelInstalled0
  !insertmacro ImportSelectedModel $ModelPackage1 $ModelSelected1 $ModelInstalled1
  !insertmacro ImportSelectedModel $ModelPackage2 $ModelSelected2 $ModelInstalled2
  !insertmacro ImportSelectedModel $ModelPackage3 $ModelSelected3 $ModelInstalled3
  !insertmacro ImportSelectedModel $ModelPackage4 $ModelSelected4 $ModelInstalled4
  !insertmacro ImportSelectedModel $ModelPackage5 $ModelSelected5 $ModelInstalled5
  !insertmacro ImportSelectedModel $ModelPackage6 $ModelSelected6 $ModelInstalled6
  !insertmacro ImportSelectedModel $ModelPackage7 $ModelSelected7 $ModelInstalled7
  RMDir "$StateDir\Incoming"
  nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage require-model --data "$StateDir"'
  Pop $0
  ${If} $0 != 0
    # A prior installation choice is not proof that the durable model record
    # still exists. Recover once through the same idempotent Worker import;
    # ordinary healthy upgrades never take this large-package path.
    StrCpy $9 0
    !insertmacro ImportSelectedModel $ModelPackage0 $ModelSelected0 $9
    !insertmacro ImportSelectedModel $ModelPackage1 $ModelSelected1 $9
    !insertmacro ImportSelectedModel $ModelPackage2 $ModelSelected2 $9
    !insertmacro ImportSelectedModel $ModelPackage3 $ModelSelected3 $9
    !insertmacro ImportSelectedModel $ModelPackage4 $ModelSelected4 $9
    !insertmacro ImportSelectedModel $ModelPackage5 $ModelSelected5 $9
    !insertmacro ImportSelectedModel $ModelPackage6 $ModelSelected6 $9
    !insertmacro ImportSelectedModel $ModelPackage7 $ModelSelected7 $9
    RMDir "$StateDir\Incoming"
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage require-model --data "$StateDir"'
    Pop $0
  ${EndIf}
  ${If} $0 != 0
    SetErrorLevel 1
    Abort "Pegasus Cerebral has no usable registered model. Select a valid package from the Models folder or repair the existing model path, then rerun setup. Program files and preserved data remain available."
  ${EndIf}
  WriteINIStr "$StateDir\installation-stage.ini" "Setup" "Stage" "models-verified"
FunctionEnd

Function PrepareComponentsPage
  ${If} $ComponentsInitialized == 1
    Return
  ${EndIf}
  StrCpy $ComponentsInitialized 1
  !insertmacro SetPluginInitial ${SecPluginAiDictation} "pegasus.ai-dictation"
  !insertmacro SetPluginInitial ${SecPluginBooks} "pegasus.books"
  !insertmacro SetPluginInitial ${SecPluginDictation} "pegasus.dictation"
  !insertmacro SetPluginInitial ${SecPluginProgramming} "pegasus.programming"
  !insertmacro SetPluginInitial ${SecPluginPromptReview} "pegasus.prompt-review"
  !insertmacro SetPluginInitial ${SecPluginBrowser} "pegasus.browser"
  !insertmacro SetPrerequisiteInitial ${SecCortexTrust} "cortex-trust"
  !insertmacro SetPrerequisiteInitial ${SecCortexCodexRegistration} "cortex-codex-registration"
  !insertmacro SetPrerequisiteInitial ${SecCortexCodexGuidance} "cortex-codex-guidance"
  !insertmacro SetBehaviorPackInitial ${SecCortexBehaviorPackCerebralGuides} "pegasus-cerebral-guides-v1"
  !insertmacro SetBehaviorPackInitial ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge-v1"
  !insertmacro SetPrerequisiteInitial ${SecPrereqDotnet} "dotnet-sdk"
  !insertmacro SetPrerequisiteInitial ${SecPrereqPowerShell} "powershell"
  !insertmacro SetPrerequisiteInitial ${SecPrereqGit} "git"
  !insertmacro SetPrerequisiteInitial ${SecPrereqNode} "node"
  !insertmacro SetPrerequisiteInitial ${SecPrereqPython} "python"
  !insertmacro SetPrerequisiteInitial ${SecPrereqSevenZip} "sevenzip"
  !insertmacro SetPrerequisiteInitial ${SecPrereqNsis} "nsis"
  !insertmacro SetPrerequisiteInitial ${SecPrereqVsBuildTools} "vs-buildtools"
  !insertmacro SetPrerequisiteInitial ${SecPrereqWsl} "wsl"
  !insertmacro SetPrerequisiteInitial ${SecCacheInstallationSource} "installation-source-cache"
  !insertmacro ApplyComponentMetadata ${SecCoreApplication} "cerebral-core" "Pegasus Cerebral application"
  !insertmacro ApplyComponentMetadata ${SecPrereqCortex} "cortex-core" "Pegasus Cortex continuity service"
  !insertmacro ApplyComponentMetadata ${SecPrereqVcRedist} "vc-redist" "Microsoft Visual C++ x64 runtime"
  !insertmacro ApplyComponentMetadata ${SecCortexTrust} "cortex-trust" "Windows trust certificate"
  !insertmacro ApplyComponentMetadata ${SecPluginAiDictation} "pegasus.ai-dictation" "AI Dictation (Parakeet, CPU)"
  !insertmacro ApplyComponentMetadata ${SecPluginBooks} "pegasus.books" "Books"
  !insertmacro ApplyComponentMetadata ${SecPluginBrowser} "pegasus.browser" "Browser"
  !insertmacro ApplyComponentMetadata ${SecPluginProgramming} "pegasus.programming" "Programming"
  !insertmacro ApplyComponentMetadata ${SecPluginPromptReview} "pegasus.prompt-review" "Prompt Review"
  !insertmacro ApplyComponentMetadata ${SecPluginDictation} "pegasus.dictation" "Windows Dictation"
  !insertmacro ApplyComponentMetadata ${SecCortexBehaviorPackCerebralGuides} "pegasus-cerebral-guides-v1" "Cerebral agent guides"
  !insertmacro ApplyComponentMetadata ${SecCortexBehaviorPackWindowsKnowledge} "windows-operational-knowledge-v1" "Windows operations guide"
  !insertmacro ApplyComponentMetadata ${SecPrereqDotnet} "dotnet-sdk" ".NET 10 SDK and desktop runtime"
  !insertmacro ApplyComponentMetadata ${SecPrereqPowerShell} "powershell" "PowerShell 7"
  !insertmacro ApplyComponentMetadata ${SecPrereqGit} "git" "Git for Windows"
  !insertmacro ApplyComponentMetadata ${SecPrereqNode} "node" "Node.js LTS"
  !insertmacro ApplyComponentMetadata ${SecPrereqPython} "python" "Python"
  !insertmacro ApplyComponentMetadata ${SecPrereqSevenZip} "sevenzip" "7-Zip"
  !insertmacro ApplyComponentMetadata ${SecPrereqNsis} "nsis" "NSIS installer builder"
  !insertmacro ApplyComponentMetadata ${SecPrereqVsBuildTools} "vs-buildtools" "Visual Studio Build Tools"
  !insertmacro ApplyComponentMetadata ${SecPrereqWsl} "wsl" "Windows Subsystem for Linux"
  !insertmacro ApplyComponentMetadata ${SecCortexCodexRegistration} "cortex-codex-registration" "Register Cortex MCP and PAO tools with Codex"
  !insertmacro ApplyComponentMetadata ${SecCortexCodexGuidance} "cortex-codex-guidance" "Add Cortex guidance to Codex AGENTS.md"
  !insertmacro ApplyComponentMetadata ${SecCacheInstallationSource} "installation-source-cache" "Keep offline installation source for repair or reinstall"
  ClearErrors
  ${GetOptions} $Args "/SKIPCORTEXINSTALL=" $SkipCortexInstallRequested
  ${IfNot} ${Errors}
    ${If} $SkipCortexInstallRequested != "0"
    ${AndIf} $SkipCortexInstallRequested != "1"
      MessageBox MB_ICONSTOP "/SKIPCORTEXINSTALL accepts only 1 (use the existing compatible Cortex without changing it) or 0 (normal behavior)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
  ${EndIf}
  ${If} $SkipCortexInstallRequested == "1"
    ${If} $Portable != 0
    ${OrIf} $ExistingLayout != 1
      MessageBox MB_ICONSTOP "/SKIPCORTEXINSTALL=1 is supported only for an in-place service update." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "Product"
    ReadINIStr $1 "$INSTDIR\installation.ini" "Cerebral" "Portable"
    ${If} $0 != "PegasusCerebral"
    ${OrIf} $1 != "0"
      MessageBox MB_ICONSTOP "/SKIPCORTEXINSTALL=1 requires the registered service installation." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
    ReadINIStr $0 "$InstallerStateFile" "componentStates" "cortex-core.state"
    ${If} $0 != "current"
    ${AndIf} $0 != "newer"
      MessageBox MB_ICONSTOP "/SKIPCORTEXINSTALL=1 requires an existing current or newer Cortex core." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
    SectionSetText ${SecCortexTrust} "Windows trust certificate - saved choice retained; Cortex setup skipped for this update"
    SectionSetText ${SecCortexCodexRegistration} "Codex registration - saved choice retained; Cortex setup skipped for this update"
    SectionSetText ${SecCortexCodexGuidance} "Codex guidance - saved choice retained; Cortex setup skipped for this update"
    SectionSetText ${SecCortexBehaviorPackCerebralGuides} "Cerebral guides - saved choice retained; import skipped for this update"
    SectionSetText ${SecCortexBehaviorPackWindowsKnowledge} "Windows operations guide - saved choice retained; import skipped for this update"
  ${EndIf}
  ClearErrors
  ${GetOptions} $Args "/SKIPPYTHONINSTALL=" $SkipPythonInstallRequested
  ${IfNot} ${Errors}
    ${If} $SkipPythonInstallRequested == "1"
      ${If} $ExistingLayout != 1
        MessageBox MB_ICONSTOP "/SKIPPYTHONINSTALL=1 is supported only for an in-place update." /SD IDOK
        SetErrorLevel 87
        Quit
      ${EndIf}
      ReadINIStr $0 "$InstallerStateFile" "componentStates" "python.state"
      ${If} $0 != "not-installed"
        MessageBox MB_ICONSTOP "/SKIPPYTHONINSTALL=1 requires Python to be absent in the installed-state check." /SD IDOK
        SetErrorLevel 87
        Quit
      ${EndIf}
      SectionSetText ${SecPrereqPython} "Python - saved selection retained; install skipped for this update"
    ${ElseIf} $SkipPythonInstallRequested != "0"
      MessageBox MB_ICONSTOP "/SKIPPYTHONINSTALL accepts only 1 (preserve the saved selection without installing Python) or 0 (normal behavior)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
  ${EndIf}
  ClearErrors
  ${GetOptions} $Args "/PRESERVEWORKERMODELS=" $PreserveWorkerModels
  ${IfNot} ${Errors}
    ${If} $PreserveWorkerModels == "1"
      IfSilent preserve_worker_models_layout
      MessageBox MB_ICONSTOP "/PRESERVEWORKERMODELS=1 is supported only for a silent in-place service update." /SD IDOK
      SetErrorLevel 87
      Quit
      preserve_worker_models_layout:
      ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "Product"
      ReadINIStr $1 "$INSTDIR\installation.ini" "Cerebral" "Portable"
      ${If} $Portable != 0
      ${OrIf} $ExistingLayout != 1
      ${OrIf} $0 != "PegasusCerebral"
      ${OrIf} $1 != "0"
        MessageBox MB_ICONSTOP "/PRESERVEWORKERMODELS=1 requires an existing registered Cerebral service installation." /SD IDOK
        SetErrorLevel 87
        Quit
      ${EndIf}
    ${ElseIf} $PreserveWorkerModels != "0"
      MessageBox MB_ICONSTOP "/PRESERVEWORKERMODELS accepts only 1 (keep the existing Worker model choices and defer model verification) or 0 (normal behavior)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
  ${EndIf}
  # Attribute the replaceable application payload to its visible required row.
  # The hidden execution section remains size zero so the component and model
  # gauges do not count the same files twice.
  ClearErrors
  ${GetSize} "$CorePayloadDirectory" "/S=0K /G=0" $0 $1 $2
  ${If} ${Errors}
    StrCpy $0 0
  ${EndIf}
  SectionSetSize ${SecCoreApplication} $0
  SectionSetSize ${Main} 0
  ClearErrors
  ${GetOptions} $Args "/CACHEINSTALLSOURCE=" $CacheInstallationSourceRequested
  ${IfNot} ${Errors}
    SectionGetFlags ${SecCacheInstallationSource} $0
    ${If} $CacheInstallationSourceRequested == "1"
      IntOp $0 $0 | ${SF_SELECTED}
    ${ElseIf} $CacheInstallationSourceRequested == "0"
      IntOp $1 ${SF_SELECTED} ~
      IntOp $0 $0 & $1
    ${Else}
      MessageBox MB_ICONSTOP "/CACHEINSTALLSOURCE accepts only 1 (keep an offline repair source) or 0 (do not keep it)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
    SectionSetFlags ${SecCacheInstallationSource} $0
  ${EndIf}
  ClearErrors
  ${GetOptions} $Args "/CORTEXBEHAVIORPACKS=" $CortexBehaviorPacksRequested
  ${IfNot} ${Errors}
    ${If} $SkipCortexInstallRequested == "1"
    ${AndIf} $CortexBehaviorPacksRequested == "1"
      MessageBox MB_ICONSTOP "/CORTEXBEHAVIORPACKS=1 conflicts with /SKIPCORTEXINSTALL=1." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
    ${If} $CortexBehaviorPacksRequested == "1"
      SectionGetFlags ${SecCortexBehaviorPackCerebralGuides} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecCortexBehaviorPackCerebralGuides} $0
      SectionGetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
    ${ElseIf} $CortexBehaviorPacksRequested == "0"
      IntOp $1 ${SF_SELECTED} ~
      SectionGetFlags ${SecCortexBehaviorPackCerebralGuides} $0
      IntOp $0 $0 & $1
      SectionSetFlags ${SecCortexBehaviorPackCerebralGuides} $0
      SectionGetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
      IntOp $0 $0 & $1
      SectionSetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
    ${Else}
      MessageBox MB_ICONSTOP "/CORTEXBEHAVIORPACKS accepts only 1 (select both bundled modules) or 0 (preserve Cortex knowledge without importing them)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
  ${EndIf}
  ClearErrors
  ${GetOptions} $Args "/BUNDLEDPLUGINS=" $BundledPluginsRequested
  ${IfNot} ${Errors}
    ${If} $BundledPluginsRequested == "all"
      SectionGetFlags ${SecPluginAiDictation} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginAiDictation} $0
      SectionGetFlags ${SecPluginBooks} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginBooks} $0
      SectionGetFlags ${SecPluginBrowser} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginBrowser} $0
      SectionGetFlags ${SecPluginProgramming} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginProgramming} $0
      SectionGetFlags ${SecPluginPromptReview} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginPromptReview} $0
      SectionGetFlags ${SecPluginDictation} $0
      IntOp $0 $0 | ${SF_SELECTED}
      SectionSetFlags ${SecPluginDictation} $0
    ${ElseIf} $BundledPluginsRequested != "preserve"
      MessageBox MB_ICONSTOP "/BUNDLEDPLUGINS accepts all (select every bundled plugin) or preserve (keep the installed selection)." /SD IDOK
      SetErrorLevel 87
      Quit
    ${EndIf}
  ${EndIf}
  # A portable extraction may read the installed Cortex prerequisite, but it
  # must not publish machine-wide knowledge modules. Keep its component marker
  # truthful as well as preventing the external mutation.
  ${If} $Portable == 1
    StrCpy $CortexBehaviorPacksRequested "0"
    IntOp $1 ${SF_SELECTED} ~
    SectionGetFlags ${SecCortexBehaviorPackCerebralGuides} $0
    IntOp $0 $0 & $1
    SectionSetFlags ${SecCortexBehaviorPackCerebralGuides} $0
    SectionGetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
    IntOp $0 $0 & $1
    SectionSetFlags ${SecCortexBehaviorPackWindowsKnowledge} $0
  ${EndIf}
FunctionEnd

!macro AddSelectedSectionSize SectionId Total
  SectionGetFlags ${SectionId} $6
  IntOp $6 $6 & ${SF_SELECTED}
  ${If} $6 == ${SF_SELECTED}
    SectionGetSize ${SectionId} $7
    IntOp ${Total} ${Total} + $7
  ${EndIf}
!macroend

Function RefreshModelSpace
  !insertmacro ReadModelOption $ModelPackage0 $ModelSelected0 $ModelCheckbox0
  !insertmacro ReadModelOption $ModelPackage1 $ModelSelected1 $ModelCheckbox1
  !insertmacro ReadModelOption $ModelPackage2 $ModelSelected2 $ModelCheckbox2
  !insertmacro ReadModelOption $ModelPackage3 $ModelSelected3 $ModelCheckbox3
  !insertmacro ReadModelOption $ModelPackage4 $ModelSelected4 $ModelCheckbox4
  !insertmacro ReadModelOption $ModelPackage5 $ModelSelected5 $ModelCheckbox5
  !insertmacro ReadModelOption $ModelPackage6 $ModelSelected6 $ModelCheckbox6
  !insertmacro ReadModelOption $ModelPackage7 $ModelSelected7 $ModelCheckbox7
  StrCpy $0 0
  !insertmacro AddSelectedModelSize $ModelPackage0 $ModelSize0 $ModelSelected0 $0
  !insertmacro AddSelectedModelSize $ModelPackage1 $ModelSize1 $ModelSelected1 $0
  !insertmacro AddSelectedModelSize $ModelPackage2 $ModelSize2 $ModelSelected2 $0
  !insertmacro AddSelectedModelSize $ModelPackage3 $ModelSize3 $ModelSelected3 $0
  !insertmacro AddSelectedModelSize $ModelPackage4 $ModelSize4 $ModelSelected4 $0
  !insertmacro AddSelectedModelSize $ModelPackage5 $ModelSize5 $ModelSelected5 $0
  !insertmacro AddSelectedModelSize $ModelPackage6 $ModelSize6 $ModelSelected6 $0
  !insertmacro AddSelectedModelSize $ModelPackage7 $ModelSize7 $ModelSelected7 $0
  IntOp $1 $0 + 1023
  IntOp $1 $1 / 1024
  StrCpy $2 0
  !insertmacro AddSelectedSectionSize ${SecCoreApplication} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqCortex} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqVcRedist} $2
  !insertmacro AddSelectedSectionSize ${SecCortexTrust} $2
  !insertmacro AddSelectedSectionSize ${SecPluginAiDictation} $2
  !insertmacro AddSelectedSectionSize ${SecPluginBooks} $2
  !insertmacro AddSelectedSectionSize ${SecPluginBrowser} $2
  !insertmacro AddSelectedSectionSize ${SecPluginProgramming} $2
  !insertmacro AddSelectedSectionSize ${SecPluginPromptReview} $2
  !insertmacro AddSelectedSectionSize ${SecPluginDictation} $2
  !insertmacro AddSelectedSectionSize ${SecCortexBehaviorPackCerebralGuides} $2
  !insertmacro AddSelectedSectionSize ${SecCortexBehaviorPackWindowsKnowledge} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqDotnet} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqPowerShell} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqGit} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqNode} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqPython} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqSevenZip} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqNsis} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqVsBuildTools} $2
  !insertmacro AddSelectedSectionSize ${SecPrereqWsl} $2
  !insertmacro AddSelectedSectionSize ${SecCacheInstallationSource} $2
  IntOp $2 $2 + 1023
  IntOp $2 $2 / 1024
  IntOp $3 $1 + $2
  ${GetRoot} "$BaseDir" $4
  ${DriveSpace} "$4" "/D=F /S=M" $5
  ${NSD_SetText} $ModelSpaceLabel "Selected installation space: approximately $3 MB ($1 MB in model packages; $2 MB in application, capabilities and prerequisites). Available on $4: $5 MB."
FunctionEnd

Function un.onInit
  SetRegView 64
  ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "Product"
  ${If} $0 != "PegasusCerebral"
    SetErrorLevel 1
    Abort "The installation marker is missing. No directories will be removed."
  ${EndIf}
  ReadINIStr $Portable "$INSTDIR\installation.ini" "Cerebral" "Portable"
  ${If} $Portable != 1
    UserInfo::GetAccountType
    Pop $0
    ${If} $0 != "admin"
      MessageBox MB_ICONSTOP "Run the uninstaller as administrator or use Integrator's Uninstall action." /SD IDOK
      SetErrorLevel 740
      Quit
    ${EndIf}
    SetShellVarContext all
  ${EndIf}
FunctionEnd

Section "Uninstall"
  ${If} $Portable != 1
    nsExec::ExecToLog '"$INSTDIR\Applications\Worker\Cerebral.Worker.exe" manage remove-service'
    Pop $0
    ${If} $0 != 0
      SetErrorLevel 1
      Abort "Could not stop/remove the worker service. No program files have been removed."
    ${EndIf}
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PegasusCerebral"
    DeleteRegKey HKLM "Software\Pegasus\Cerebral"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PegasusCerebralConfiguration"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "PegasusCerebral"
    ReadRegStr $0 HKLM "Software\Classes\.CerebralPlugin" ""
    ${If} $0 == "Pegasus.Cerebral.PluginPackage"
      DeleteRegKey HKLM "Software\Classes\.CerebralPlugin"
    ${EndIf}
    DeleteRegKey HKLM "Software\Classes\Pegasus.Cerebral.PluginPackage"
    ReadRegStr $0 HKLM "Software\Classes\.CerebralLLM" ""
    ${If} $0 == "Pegasus.Cerebral.ModelPackage"
      DeleteRegKey HKLM "Software\Classes\.CerebralLLM"
    ${EndIf}
    DeleteRegKey HKLM "Software\Classes\Pegasus.Cerebral.ModelPackage"
    ReadRegStr $0 HKLM "Software\Classes\.CerebralWorker" ""
    ${If} $0 == "Pegasus.Cerebral.WorkerInvitation"
      DeleteRegKey HKLM "Software\Classes\.CerebralWorker"
    ${EndIf}
    DeleteRegKey HKLM "Software\Classes\Pegasus.Cerebral.WorkerInvitation"
    System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
    Delete "$SMPROGRAMS\Pegasus Cerebral\Integrator.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Orchestrator.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral Configuration.lnk"
    Delete "$SMPROGRAMS\Pegasus Cerebral\Pegasus Cerebral.lnk"
    ReadINIStr $0 "$INSTDIR\installation.ini" "Cerebral" "DesktopShortcuts"
    ${If} $0 == 1
      Delete "$DESKTOP\Pegasus Cerebral Configuration.lnk"
      Delete "$DESKTOP\Pegasus Cerebral.lnk"
    ${EndIf}
    RMDir "$SMPROGRAMS\Pegasus Cerebral"
  ${EndIf}
  # The Program root contains only replaceable application files. Models, data,
  # tools, projects, shared files and user profiles are separate sibling roots.
  ClearErrors
  RMDir /r "$INSTDIR"
  ${If} ${Errors}
    # NSIS normally runs the uninstaller from a temporary copy, but bounded
    # acceptance and repair owners may use _?= to keep execution in place and
    # receive the real completion code. In that mode the running Uninstall.exe
    # is the only file Windows may legitimately retain. Fail if any other file
    # or directory survived the recursive removal.
    ClearErrors
    StrCpy $2 0
    FindFirst $0 $1 "$INSTDIR\*"
    ${IfNot} ${Errors}
      uninstall_check_remaining:
      ${If} $1 == ""
        Goto uninstall_check_done
      ${EndIf}
      ${If} $1 != "."
      ${AndIf} $1 != ".."
      ${AndIf} $1 != "Uninstall.exe"
        StrCpy $2 1
        Goto uninstall_check_done
      ${EndIf}
      ClearErrors
      FindNext $0 $1
      ${If} ${Errors}
        StrCpy $1 ""
      ${EndIf}
      Goto uninstall_check_remaining
      uninstall_check_done:
      FindClose $0
    ${EndIf}
    ${If} $2 == 1
      SetErrorLevel 1
      Abort "Some program files could not be removed. Close the applications and rerun this uninstaller. Application data remains untouched."
    ${EndIf}
    ClearErrors
  ${EndIf}
  SetErrorLevel 0
SectionEnd
