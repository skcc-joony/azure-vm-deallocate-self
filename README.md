# azure-vm-deallocate-self

다음 문서는 Azure Virtual Desktop(AVD) 세션 호스트에서 사용자가 한 번의 클릭으로 자신의 VM을 Stopped (deallocated) 상태로 전환할 수 있도록 구축‧배포한 'DeallocateSelf.exe' 프로젝트의 전 과정을 정리한 것입니다. IMDS(169.254.169.254) · Managed Identity · Azure RBAC Custom Role · .NET 8 단일 실행 파일 빌드 · 그룹 정책(GPO) 배포 · 문제 해결·검증까지, 실제 시행한 명령과 설정을 모두 포함합니다.

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
### 3.2 'Deallocate 전용' Custom Role 생성
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

### 4.1 신규 프로젝트 생성 (새로 시작하는 경우)
프로젝트를 처음부터 생성하는 경우의 과정입니다:

```bash
# 1. 콘솔 프로젝트 생성
dotnet new console -n DeallocateSelf
cd DeallocateSelf

# 2. 필수 패키지 추가
dotnet add package Azure.Identity                  # MI 인증
dotnet add package Azure.ResourceManager.Compute   # VM 제어 SDK

# 3. 패키지 복원 (자동으로 실행되지만 명시적 확인)
dotnet restore
```

### 4.2 기존 프로젝트 빌드 (코드가 이미 있는 경우)
기존 프로젝트를 받아서 빌드하는 경우의 과정입니다:

```bash
# 1. 패키지 의존성 복원 (필수)
dotnet restore

# 2. 빌드 테스트 - 컴파일 오류 확인 (권장)
dotnet build

# 3. 릴리즈 빌드로 최종 게시 (필수)
dotnet publish -c Release
```

### 4.3 프로젝트 설정 파일 (DeallocateSelf.csproj)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- ① 반드시 실행-파일(EXE)로 지정 → CS8805 해결 -->
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>

    <!-- ② 완전 단일-파일(Self-contained) 빌드 -->
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>

    <!-- ③ 기본 using(System, Http 등) 자동 포함 → CS0246, CS0103 해결 -->
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Azure.Identity" Version="1.*" />
    <PackageReference Include="Azure.ResourceManager.Compute" Version="1.*" />
  </ItemGroup>
</Project>
```

### 4.4 빌드 명령어 옵션 설명

#### 간단한 빌드 명령어 (권장)
```bash
dotnet publish -c Release
```
**왜 이렇게 간단한가?** 
- `.csproj` 파일에 모든 필요한 설정이 이미 정의되어 있기 때문입니다.

#### 상세한 빌드 명령어 (명시적)
```bash
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

#### 명령어 옵션과 .csproj 설정 대응표
| 명령어 옵션 | .csproj 설정 | 설명 |
|------------|-------------|------|
| `-r win-x64` | `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` | Windows x64 타겟 |
| `--self-contained` | `<SelfContained>true</SelfContained>` | .NET 런타임 포함 |
| `-p:PublishSingleFile=true` | `<PublishSingleFile>true</PublishSingleFile>` | 단일 실행 파일 |
| `-p:IncludeNativeLibrariesForSelfExtract=true` | `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>` | 네이티브 라이브러리 포함 |

**결론**: `.csproj`에 설정이 있으면 명령어에서 생략 가능하므로, 간단한 `dotnet publish -c Release` 명령어로 충분합니다.

### 4.5 Program.cs 요약
```csharp
const string imds="http://169.254.169.254/metadata/instance?api-version=2021-02-01";
var http=new HttpClient(new HttpClientHandler{Proxy=null});
http.DefaultRequestHeaders.Add("Metadata","true");                 // IMDS 요구 헤더
var meta=await http.GetFromJsonAsync<JsonElement>(imds);
string sub=meta.GetProperty("compute").GetProperty("subscriptionId").GetString()!;
string rg =meta.GetProperty("compute").GetProperty("resourceGroupName").GetString()!;
string vm =meta.GetProperty("compute").GetProperty("name").GetString()!;
var arm=new ArmClient(new DefaultAzureCredential());
var vmRes=arm.GetVirtualMachineResource(
    VirtualMachineResource.CreateResourceIdentifier(sub,rg,vm));
await vmRes.DeallocateAsync(Azure.WaitUntil.Completed);            // REST 호출
```

### 4.6 빌드 결과 확인
빌드가 성공하면 다음 위치에 파일이 생성됩니다:
```
bin/Release/net8.0/win-x64/publish/
├── DeallocateSelf.exe  (약 72MB - 모든 의존성 포함)
└── DeallocateSelf.pdb  (디버그 정보)
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
