; Picked up by electron-builder's NSIS installer from the build resources folder (build/installer.nsh).

; "Start when I sign in" is registered by Electron (app.setLoginItemSettings) under the current user's
; Run key, which uninstalling does not touch: Windows would keep listing the app in Startup apps and
; try to start the missing program at every sign-in. The value is named after the app's
; AppUserModelId, which Electron makes "electron.app." and the app's name: the package name, as
; the packaged package.json has no productName; the product name is removed too in case that changes.
; Not on an update, which runs the old version's uninstaller and keeps the setting.
!macro customUnInstall
  ${ifNot} ${isUpdated}
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "electron.app.${APP_PACKAGE_NAME}"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" "electron.app.${APP_PACKAGE_NAME}"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "electron.app.${PRODUCT_NAME}"
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" "electron.app.${PRODUCT_NAME}"
  ${endIf}
!macroend
