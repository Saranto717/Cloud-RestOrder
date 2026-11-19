#ifndef PublishDir
  #error PublishDir must point to the self-contained win-x64 publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "17.3.131"
#endif
[Setup]
AppId={{7A8E523B-37BA-40E2-A517-2DAD366A04E7}
AppName=RestaurantOrders
AppVersion={#AppVersion}
AppPublisher=RestaurantOrders
DefaultDirName={autopf}\RestaurantOrders
DefaultGroupName=RestaurantOrders
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#OutputDir}
OutputBaseFilename=RestaurantOrders-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\RestaurantOrders.exe
InfoBeforeFile=Customer-Guide-EL.txt

[Languages]
Name: "greek"; MessagesFile: "compiler:Default.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[LangOptions]
greek.LanguageName=Ελληνικά
greek.LanguageID=$0408

[Messages]
greek.SetupWindowTitle=Εγκατάσταση - %1
greek.ButtonBack=< &Πίσω
greek.ButtonNext=&Επόμενο >
greek.ButtonInstall=&Εγκατάσταση
greek.ButtonCancel=Ά&κυρο
greek.ButtonYes=&Ναι
greek.ButtonNo=Ό&χι
greek.ButtonFinish=&Τέλος
greek.ButtonBrowse=&Αναζήτηση...
greek.ButtonWizardBrowse=&Αναζήτηση...
greek.WelcomeLabel1=Καλώς ήρθες στην εγκατάσταση του [name]
greek.WelcomeLabel2=Θα εγκατασταθεί το [name/ver] στον υπολογιστή σου.%n%nΚλείσε την προηγούμενη εφαρμογή πριν συνεχίσεις.
greek.WizardInfoBefore=Οδηγίες εγκατάστασης
greek.InfoBeforeLabel=Διάβασε τις ακόλουθες πληροφορίες πριν συνεχίσεις.
greek.InfoBeforeClickLabel=Πάτησε Επόμενο για να συνεχίσεις.
greek.WizardSelectDir=Φάκελος εγκατάστασης
greek.SelectDirDesc=Σε ποιον φάκελο θα εγκατασταθεί το [name];
greek.SelectDirLabel3=Τα αρχεία εφαρμογής θα εγκατασταθούν στον ακόλουθο φάκελο.
greek.SelectDirBrowseLabel=Πάτησε Επόμενο ή Αναζήτηση για διαφορετικό φάκελο.
greek.WizardReady=Έτοιμο για εγκατάσταση
greek.ReadyLabel1=Είναι όλα έτοιμα για την εγκατάσταση του [name].
greek.ReadyLabel2a=Πάτησε Εγκατάσταση για να συνεχίσεις ή Πίσω για αλλαγή επιλογών.
greek.ReadyLabel2b=Πάτησε Εγκατάσταση για να συνεχίσεις.
greek.ReadyMemoDir=Φάκελος εγκατάστασης:
greek.WizardPreparing=Προετοιμασία εγκατάστασης
greek.WizardInstalling=Εγκατάσταση
greek.InstallingLabel=Περίμενε όσο εγκαθίσταται το [name].
greek.FinishedHeadingLabel=Ολοκλήρωση εγκατάστασης του [name]
greek.FinishedLabel=Το [name] εγκαταστάθηκε. Άνοιξε την εφαρμογή από τη συντόμευση στην επιφάνεια εργασίας.
greek.FinishedLabelNoIcons=Η εγκατάσταση του [name] ολοκληρώθηκε.

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Prepare-Service.ps1"; Flags: dontcopy
Source: "ServiceSetup.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "Read-Database-Location.ps1"; Flags: dontcopy
Source: "RestaurantOrders.url"; DestDir: "{app}"; Flags: ignoreversion
Source: "Customer-Guide-EL.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commondesktop}\RestaurantOrders"; Filename: "{app}\RestaurantOrders.url"; IconFilename: "{app}\RestaurantOrders.exe"
Name: "{group}\RestaurantOrders"; Filename: "{app}\RestaurantOrders.url"; IconFilename: "{app}\RestaurantOrders.exe"
Name: "{group}\Πρόσβαση από κινητά και tablets"; Filename: "{app}\Access.html"
Name: "{group}\Οδηγίες"; Filename: "{app}\Customer-Guide-EL.txt"

[Run]
Filename: "{app}\RestaurantOrders.url"; Description: "Άνοιγμα RestaurantOrders"; Flags: shellexec postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\ServiceSetup.ps1"" -Action Uninstall -InstallDirectory ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRestaurantOrdersService"

[UninstallDelete]
Type: files; Name: "{app}\Access.html"

[Code]
var
  DatabasePage: TInputFileWizardPage;
  DatabaseDirectoryPage: TInputDirWizardPage;
  DatabaseFilePage: TInputQueryWizardPage;
  EditionPage: TInputOptionWizardPage;
  ExistingTakeAwayOnly: Cardinal;

function RunScript(const ScriptPath, Arguments: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

procedure InitializeWizard;
var
  ExitCode: Integer;
  TempOutput: String;
  LocationLines: TArrayOfString;
begin
  EditionPage := CreateInputOptionPage(wpWelcome, 'Επιλογή έκδοσης',
    'Διάλεξε τις λειτουργίες που θα εμφανίζονται σε αυτή την εγκατάσταση',
    'Η επιλογή μπορεί να αλλάξει σε επόμενη αναβάθμιση.', True, False);
  EditionPage.Add('Μόνο Take Away');
  EditionPage.Add('Παραγγελιοληψία και Take Away (όπως σήμερα)');
  EditionPage.SelectedValueIndex := 1;
  if RegQueryDWordValue(HKLM64, 'Software\RestaurantOrders', 'TakeAwayOnly', ExistingTakeAwayOnly) and (ExistingTakeAwayOnly = 1) then
    EditionPage.SelectedValueIndex := 0;

  DatabaseDirectoryPage := CreateInputDirPage(wpSelectDir, 'Θέση τοπικής βάσης',
    'Διάλεξε φάκελο για τη βάση του καταστήματος',
    'Η βάση SQLite θα αποθηκευτεί εδώ. Η υπηρεσία RestaurantOrders χρειάζεται δικαίωμα εγγραφής στον φάκελο.', False, 'Νέος φάκελος');
  DatabaseDirectoryPage.Add('Φάκελος βάσης δεδομένων:');
  DatabaseDirectoryPage.Values[0] := ExpandConstant('{commonappdata}\RestaurantOrders');

  DatabaseFilePage := CreateInputQueryPage(DatabaseDirectoryPage.ID, 'Όνομα τοπικής βάσης',
    'Δώσε ένα όνομα που να αναγνωρίζεις για το κατάστημα',
    'Το πρόγραμμα θα προσθέσει το όνομα αυτό στη διαδρομή και θα χρησιμοποιεί την ίδια βάση μετά από κάθε επανεκκίνηση.');
  DatabaseFilePage.Add('Όνομα αρχείου βάσης (.db):', False);
  DatabaseFilePage.Values[0] := 'restaurant.db';

  DatabasePage := CreateInputFilePage(DatabaseFilePage.ID, 'Υπάρχουσα βάση δεδομένων',
    'Προαιρετική μεταφορά δεδομένων από προηγούμενη εγκατάσταση',
    'Για νέα εγκατάσταση άφησε το πεδίο κενό. Για μεταφορά, επίλεξε την υπάρχουσα βάση SQLite. Το αρχικό αρχείο διατηρείται.');
  DatabasePage.Add('Βάση δεδομένων (προαιρετικά):', 'SQLite database|*.db|All files|*.*', '.db');

  ExtractTemporaryFile('Read-Database-Location.ps1');
  TempOutput := ExpandConstant('{tmp}\restaurantorders-database-location.txt');
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Read-Database-Location.ps1') +
    '" -DataDirectory "' + ExpandConstant('{commonappdata}\RestaurantOrders') + '" -OutputFile "' + TempOutput + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0) and
    LoadStringsFromFile(TempOutput, LocationLines) and (GetArrayLength(LocationLines) >= 2) then
  begin
    DatabaseDirectoryPage.Values[0] := LocationLines[0];
    DatabaseFilePage.Values[0] := LocationLines[1];
  end
  else
  begin
    if RegQueryStringValue(HKLM64, 'Software\RestaurantOrders', 'DatabaseDirectory', TempOutput) then
      DatabaseDirectoryPage.Values[0] := TempOutput;
    if RegQueryStringValue(HKLM64, 'Software\RestaurantOrders', 'DatabaseFileName', TempOutput) then
      DatabaseFilePage.Values[0] := TempOutput;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = DatabasePage.ID) and
    FileExists(AddBackslash(DatabaseDirectoryPage.Values[0]) + DatabaseFilePage.Values[0]);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = DatabaseDirectoryPage.ID) and (Trim(DatabaseDirectoryPage.Values[0]) = '') then
  begin
    MsgBox('Επίλεξε φάκελο για τη βάση δεδομένων.', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = DatabaseFilePage.ID) and
    ((Trim(DatabaseFilePage.Values[0]) = '') or (ExtractFileName(DatabaseFilePage.Values[0]) <> DatabaseFilePage.Values[0]) or
     (CompareText(ExtractFileExt(DatabaseFilePage.Values[0]), '.db') <> 0)) then
  begin
    MsgBox('Δώσε μόνο όνομα αρχείου που να τελειώνει σε .db, π.χ. Taverna.db.', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = DatabasePage.ID) and (DatabasePage.Values[0] <> '') and not FileExists(DatabasePage.Values[0]) then
  begin
    MsgBox('Το επιλεγμένο αρχείο βάσης δεν βρέθηκε.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Prepare-Service.ps1');
  if not RunScript(ExpandConstant('{tmp}\Prepare-Service.ps1'), '', ExitCode) or (ExitCode <> 0) then
    Result := 'Δεν σταμάτησε η προηγούμενη υπηρεσία RestaurantOrders. Κλείσε την εφαρμογή και δοκίμασε ξανά.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  Arguments: String;
begin
  if CurStep = ssPostInstall then
  begin
    if EditionPage.SelectedValueIndex = 0 then
      Arguments := '-Action Install -Edition TakeAwayOnly -InstallDirectory "' + ExpandConstant('{app}') + '"'
    else
      Arguments := '-Action Install -Edition Full -InstallDirectory "' + ExpandConstant('{app}') + '"';
    Arguments := Arguments + ' -DatabaseDirectory "' + DatabaseDirectoryPage.Values[0] + '" -DatabaseFileName "' + DatabaseFilePage.Values[0] + '"';
    if not FileExists(AddBackslash(DatabaseDirectoryPage.Values[0]) + DatabaseFilePage.Values[0]) and (DatabasePage.Values[0] <> '') then
      Arguments := Arguments + ' -ImportDatabase "' + DatabasePage.Values[0] + '"';
    if not RunScript(ExpandConstant('{app}\installer\ServiceSetup.ps1'), Arguments, ExitCode) or (ExitCode <> 0) then
      RaiseException('Δεν ολοκληρώθηκε η εκκίνηση της υπηρεσίας. Δες C:\ProgramData\RestaurantOrders\installer-error.log και δοκίμασε ξανά το Setup.');
  end;
end;
