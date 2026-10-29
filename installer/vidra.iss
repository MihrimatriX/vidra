; Vidra kurulumu (Inno Setup 6). Doğrudan değil, installer/build.ps1 üzerinden derlenir:
; o önce tek dosyalık exe'yi publish/ klasörüne üretir, sürümü Directory.Build.props'tan okuyup /DAppVersion ile verir.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; AppId kurulumu tanımlar: güncellemeler aynı kaydın üzerine kurulur. Değiştirme.
AppId={{6F1C7A2E-3B8D-4E5A-9C21-7D4B0E8A5F13}
AppName=Vidra
AppVersion={#AppVersion}
AppVerName=Vidra {#AppVersion}
AppPublisher=Vidra
VersionInfoVersion={#AppVersion}
; Uygulama yalnızca kullanıcı profiline yazar (%APPDATA%, %LOCALAPPDATA%): yönetici izni gerekmez.
; {autopf} kullanıcı kurulumunda %LOCALAPPDATA%\Programs olur; isteyen tüm kullanıcılar için kurmayı seçebilir.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Vidra
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\Vidra.App\Assets\vidra.ico
UninstallDisplayIcon={app}\Vidra.exe
UninstallDisplayName=Vidra
OutputDir=..\dist
OutputBaseFilename=Vidra-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Güncelleme sırasında açık (tepsideki dahil) Vidra'yı fark et ve kapat
AppMutex=Vidra.SingleInstance.v1
CloseApplications=yes

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Tek dosyalık exe her şeyi içerir; publish/ içindeki .xml (WebView2 belgeleri) ve .pdb dosyalarına gerek yok
Source: "..\publish\Vidra.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Vidra"; Filename: "{app}\Vidra.exe"
Name: "{autodesktop}\Vidra"; Filename: "{app}\Vidra.exe"; Tasks: desktopicon

[Registry]
; Tarayıcı eklentisinin native messaging kaydını Vidra her açılışta kendisi yazar; kaldırınca silinsin
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.vidra.app"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.vidra.app"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vidra.app"; ValueType: none; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Chromium\NativeMessagingHosts\com.vidra.app"; ValueType: none; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Vidra.exe"; Description: "{cm:LaunchProgram,Vidra}"; Flags: nowait postinstall skipifsilent

; Kaldırma ayarları, geçmişi ve indirilen bileşenleri (%APPDATA%\Vidra, %LOCALAPPDATA%\Vidra) bilerek silmez:
; yeniden kurunca her şey yerinde olur. İndirilen videolar zaten kullanıcının klasöründe.
