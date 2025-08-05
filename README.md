# azure-vm-deallocate-self

다음 문서는 Azure Virtual Desktop(AVD) 세션 호스트에서 사용자가 한 번의 클릭으로 자신의 VM을 Stopped (deallocated) 상태로 전환할 수 있도록 구축‧배포한 ‘DeallocateSelf.exe’ 프로젝트의 전 과정을 정리한 것입니다. IMDS(169.254.169.254) · Managed Identity · Azure RBAC Custom Role · .NET 8 단일 실행 파일 빌드 · 그룹 정책(GPO) 배포 · 문제 해결·검증까지, 실제 시행한 명령과 설정을 모두 포함합니다.

## 1 아키텍처 개요
AVD 세션 호스트 VM 내부에서 DeallocateSelf.exe 가 실행되면

1. Azure Instance Metadata Service(IMDS) 169.254.169.254 에서 VM ID·구독·리소스 그룹을 조회한다.
2. VM의 System-assigned Managed Identity가 Azure Resource Manager에 로그인한다.
3. SDK 메서드 VirtualMachineResource.DeallocateAsync() → REST POST …/virtualMachines/{vm}/deallocate 를 호출한다
4. Azure Activity Log에 virtualMachines/deallocate 이벤트가 기록되고 Power State가 Stopped (deallocated) 로 바뀐다.

## 2 사전 준비
항목 | 내용
VM OS	| Windows 10/11 Enterprise multi-session 또는 Server 2022
.NET SDK |	.NET 8 설치 및 PATH 등록
네트워크	| 169.254.169.254 로컬 트래픽이 프록시를 우회하도록 no_proxy 설정

## 3 Azure 측 작업
### 3.1 Managed Identity 활성화
1. Azure Portal › VM › Identity › System assigned → On + Save 
### 3.2 ‘Deallocate 전용’ Custom Role 생성
``` jsonc
{
  "Name": "VM.Deallocate",
  "AssignableScopes": [
    "/subscriptions/<subId>/resourceGroups/<rg>/providers/Microsoft.Compute/virtualMachines/<vm>"
  ],
  "Permissions": [
    {
      "Actions": [
        "Microsoft.Compute/virtualMachines/deallocate/action"
      ]
    }
  ]
}

```
이 JSON은 정확히 1 개의 액션만 허용해 실수로 VM을 삭제·변경할 위험을 없앱니다.
### 3.3 Role Assignment
- VM › Access control (IAM) › + Add role assignment
  - Role = VM.Deallocate
  - Assign access to = Managed identity
  - Select members = 해당 VM (MI) 선택

## 4 도구 개발 (.NET 8)
### 4.1 프로젝트 & 패키지
``` bash
dotnet new console -n DeallocateSelf
cd DeallocateSelf
dotnet add package Azure.Identity                  # MI 인증 :contentReference[oaicite:6]{index=6}
dotnet add package Azure.ResourceManager.Compute   # VM 제어 SDK :contentReference[oaicite:7]{index=7}
```
### 4.2 DeallocateSelf.csproj
```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>           <!-- CS8805 해결 :contentReference[oaicite:8]{index=8} -->
  <TargetFramework>net8.0</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <ImplicitUsings>enable</ImplicitUsings> <!-- 기본 using 자동 :contentReference[oaicite:9]{index=9} -->
</PropertyGroup>
```
### 4.3 Program.cs 요약
```csharp
const string imds="http://169.254.169.254/metadata/instance?api-version=2021-02-01";
var http=new HttpClient(new HttpClientHandler{Proxy=null});
http.DefaultRequestHeaders.Add("Metadata","true");                 // IMDS 요구 헤더 :contentReference[oaicite:10]{index=10}
var meta=await http.GetFromJsonAsync<JsonElement>(imds);
string sub=meta.GetProperty("compute").GetProperty("subscriptionId").GetString()!;
string rg =meta.GetProperty("compute").GetProperty("resourceGroupName").GetString()!;
string vm =meta.GetProperty("compute").GetProperty("name").GetString()!;
var arm=new ArmClient(new DefaultAzureCredential());
var vmRes=arm.GetVirtualMachineResource(
    VirtualMachineResource.CreateResourceIdentifier(sub,rg,vm));
await vmRes.DeallocateAsync(Azure.WaitUntil.Completed);            // REST 호출 :contentReference[oaicite:11]{index=11}
```
### 4.4 단일 실행 파일 Publish
```bash
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
## 5 그룹 정책(GPO) 배포
### 5.1 Startup Script – exe 복사
CopyDeallocateTool.cmd (SYSVOL 공유):
```cmd
@echo off
set "DEST=C:\Program Files\AVDtools"
if not exist "%DEST%" mkdir "%DEST%"
copy "\\filesvr\AVDtools\DeallocateSelf.exe" "%DEST%\" /Y
exit /b 0
```
- GPMC › Computer Configuration › Policies › Windows Settings › Scripts (Startup) 에 등록 
## 5.2 바탕화면 바로가기 – GPP Shortcut
- Computer Configuration › Preferences › Windows Settings › Shortcuts
  - Action = Create
  - Location = All Users Desktop
  - Target = C:\Program Files\AVDtools\DeallocateSelf.exe
