param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Report, [switch]$Nested)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class IslandLifetimeTest {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct Startup {
  public uint Size; public string Reserved,Desktop,Title;
  public uint X,Y,Width,Height,XChars,YChars,Fill,Flags;
  public ushort ShowWindow,ReservedSize; public IntPtr ReservedBytes,Input,Output,Error;
 }
 [StructLayout(LayoutKind.Sequential)] public struct Child {public IntPtr Process,Thread;public uint Id,ThreadId;}
 [DllImport("kernel32.dll",SetLastError=true)] public static extern IntPtr CreateJobObject(IntPtr security,string name);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr data,uint size);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool CreateProcess(string exe,StringBuilder command,IntPtr ps,IntPtr ts,bool inherit,uint flags,IntPtr env,string directory,ref Startup startup,out Child child);
 [DllImport("kernel32.dll",SetLastError=true)] public static extern uint ResumeThread(IntPtr thread);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
 public static IntPtr LimitGroup(uint flags) {
  IntPtr job=CreateJobObject(IntPtr.Zero,null),buffer=Marshal.AllocHGlobal(144);
  try {for(int i=0;i<144;i++)Marshal.WriteByte(buffer,i,0);Marshal.WriteInt32(buffer,16,(int)flags);
   if(!SetInformationJobObject(job,9,buffer,144))throw new System.ComponentModel.Win32Exception();return job;
  } finally {Marshal.FreeHGlobal(buffer);}
 }
}
'@
$islandLifetimeReady = Join-Path $env:TEMP ('Island-lifetime-' + [guid]::NewGuid() + '.pid')
$islandLifetimeStop = $islandLifetimeReady + '.stop'
$islandLifetimeJob = [IslandLifetimeTest]::LimitGroup(0x2000 -bor 0x800)
$islandLifetimeInnerJob = if ($Nested) { [IslandLifetimeTest]::LimitGroup(0x800) } else { [IntPtr]::Zero }
$islandLifetimeChild = New-Object IslandLifetimeTest+Child
$islandLifetimeProbe = $null
try {
 $islandLifetimeStartup = New-Object IslandLifetimeTest+Startup
 $islandLifetimeStartup.Size = [Runtime.InteropServices.Marshal]::SizeOf($islandLifetimeStartup)
 $islandLifetimeStartup.Flags = 1
 $islandLifetimeCommand = [Text.StringBuilder]::new('"' + $Executable + '" --lifetime-hold "' + $islandLifetimeReady + '" "' + $islandLifetimeStop + '"')
 if(-not [IslandLifetimeTest]::CreateProcess($Executable,$islandLifetimeCommand,[IntPtr]::Zero,[IntPtr]::Zero,$false,4,[IntPtr]::Zero,(Split-Path $Executable),[ref]$islandLifetimeStartup,[ref]$islandLifetimeChild)) {throw 'Could not launch suspended lifetime fixture.'}
 if(-not [IslandLifetimeTest]::AssignProcessToJobObject($islandLifetimeJob,$islandLifetimeChild.Process)) {throw 'Could not assign lifetime fixture to the test job.'}
 if($Nested -and -not [IslandLifetimeTest]::AssignProcessToJobObject($islandLifetimeInnerJob,$islandLifetimeChild.Process)) {throw 'Could not assign lifetime fixture to the nested job.'}
 [IslandLifetimeTest]::ResumeThread($islandLifetimeChild.Thread) | Out-Null
 $islandLifetimeDeadline = (Get-Date).AddSeconds(10)
 while(-not (Test-Path -LiteralPath $islandLifetimeReady) -and (Get-Date) -lt $islandLifetimeDeadline) {Start-Sleep -Milliseconds 100}
 if(-not (Test-Path -LiteralPath $islandLifetimeReady)) {throw 'Lifetime fixture did not become ready.'}
 $islandLifetimeId = [int](Get-Content -LiteralPath $islandLifetimeReady)
 $islandLifetimeProbe = Get-Process -Id $islandLifetimeId
 $islandLifetimeMetadata = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $islandLifetimeId)
 $islandLifetimeParent = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $islandLifetimeMetadata.ParentProcessId)
 $islandLifetimeBrokered = $islandLifetimeParent.Name -eq 'WmiPrvSE.exe'
 $islandLifetimeSameProfile = (Get-Content -LiteralPath ($islandLifetimeReady + '.profile')) -eq [Environment]::GetFolderPath('LocalApplicationData')
 $islandLifetimeInJob = $false
 if(-not [IslandLifetimeTest]::IsProcessInJob($islandLifetimeProbe.Handle,$islandLifetimeJob,[ref]$islandLifetimeInJob)) {throw 'Could not query fixture job membership.'}
 [IslandLifetimeTest]::CloseHandle($islandLifetimeJob) | Out-Null
 $islandLifetimeJob = [IntPtr]::Zero
 $islandLifetimeSurvives = -not $islandLifetimeProbe.WaitForExit(1000)
 Set-Content -LiteralPath $islandLifetimeStop -Value 'stop'
 $islandLifetimeCleanExit = $islandLifetimeProbe.WaitForExit(3000) -and $islandLifetimeProbe.ExitCode -eq 0
 $islandLifetimeResult = [pscustomobject]@{passed=($islandLifetimeSameProfile -and $islandLifetimeBrokered -and !$islandLifetimeInJob -and $islandLifetimeSurvives -and $islandLifetimeCleanExit);sameUserProfile=$islandLifetimeSameProfile;launchedByWindowsService=$islandLifetimeBrokered;outsideTemporaryJob=(!$islandLifetimeInJob);survivesJobClosure=$islandLifetimeSurvives;exitsWhenRequested=$islandLifetimeCleanExit}
 $islandLifetimeResult | ConvertTo-Json | Set-Content -LiteralPath $Report
 $islandLifetimeResult | ConvertTo-Json
 if(-not $islandLifetimeResult.passed) {throw 'Process lifetime regression failed.'}
} finally {
 if($islandLifetimeInnerJob -ne [IntPtr]::Zero) {[IslandLifetimeTest]::CloseHandle($islandLifetimeInnerJob)|Out-Null}
 if($islandLifetimeJob -ne [IntPtr]::Zero) {[IslandLifetimeTest]::CloseHandle($islandLifetimeJob)|Out-Null}
 if($islandLifetimeChild.Thread -ne [IntPtr]::Zero) {[IslandLifetimeTest]::CloseHandle($islandLifetimeChild.Thread)|Out-Null}
 if($islandLifetimeChild.Process -ne [IntPtr]::Zero) {[IslandLifetimeTest]::CloseHandle($islandLifetimeChild.Process)|Out-Null}
 if($islandLifetimeProbe -and -not $islandLifetimeProbe.HasExited) {Set-Content -LiteralPath $islandLifetimeStop -Value 'stop';$islandLifetimeProbe.WaitForExit(3000)|Out-Null}
 Remove-Item -LiteralPath $islandLifetimeReady,$islandLifetimeStop,($islandLifetimeReady + '.profile') -ErrorAction SilentlyContinue
}
