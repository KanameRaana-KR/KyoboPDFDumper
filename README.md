# KyoboPDFDumper

교보문고 전자도서관 뷰어(`KyoboBook.Ebook.ELibrary.exe`)에서 **본인이 대출·구매·소장 중인 DRM 도서(PDF, EPUB, Comic)** 를 원본 그대로 추출하는 .NET 도구.

.NET Framework CLR의 `APPDOMAIN_MANAGER_ASM` / `APPDOMAIN_MANAGER_TYPE` 환경변수를 통해 뷰어 프로세스 시작 시 관리형 DLL(`KyoboDumper.dll`)을 인젝션하고, **Harmony** 로 뷰어 내부의 DRM 및 파일 추출 파이프라인을 후킹하여 뷰어가 메모리에 복호화한 **PDF 원본**, **EPUB 완본 패키지**, **만화(Comic) 초고화질 CBZ** 를 책 클릭 한 번에 1~2초 만에 자동으로 추출하여 저장합니다.

---

## ✨ 특징

- **수동 페이지 넘김 불필요 (One-Shot 추출)**: 사용자가 책을 한 장 한 장 넘길 필요 없이, 서재에서 도서를 클릭하여 여는 순간 1~2초 만에 전 권 분량이 한 번에 덤프됩니다.
- **원본 PDF 그대로**: 화면 캡처나 재인코딩이 아닌, 메모리 내부에서 파수(Fasoo) DRM이 풀린 **순수 원본 %PDF** 바이너리를 직접 획득
- **EPUB 완본 표준 패키징**: 
  - 뷰어가 임시 디렉토리에 전개한 파일 중 파수(Fasoo) 2차 DRM(`\x9b DRMONE`)으로 암호화된 모든 챕터(`.xhtml`)를 `DrmService.OpenSecondDrmContent`로 인-플레이스 일괄 복호화
  - IDPF EPUB 표준 규격(`mimetype` 무압축 선두 배치 + `META-INF`, `OEBPS` 압축)에 맞춰 Calibre, Apple Books 등에서 정식으로 열리는 **완전한 <도서명>.epub** 파일로 자동 재패키징
- **Comic(만화) 초고화질 CBZ 자동 생성**:
  - 교보 자체 2차 DRM 헤더(`KyoboDRMv1.0.0`)에서 16바이트 AES Key와 IV를 자동 추출
  - 본문 이미지 전체를 **AES-128-CBC (PKCS7)** 로 일괄 복호화
  - 하단 슬라이더용 저화질 썸네일(`thumb/`, 10~17KB)을 자동으로 완벽 필터링하고, **순수 초고화질 원본 페이지(`img/`, 1~3MB)** 만 자연 정렬(Natural Sort)하여 **<도서명>.cbz** 로 자동 패키징
- **도서 메타데이터 자동 파일명 지정**: 뷰어 내부의 `IBookMedia.Content.Title`을 참조하여 공백/특수문자가 정제된 의미 있는 파일명으로 자동 저장
- **원천 격리 설계**: 기존 코드베이스나 뷰어 본체를 손상시키지 않고 안전하게 독립 구동

---

## 📋 요구 사항

| 항목 | 버전 |
| --- | --- |
| OS | Windows 10/11 (x64 / x86) |
| .NET SDK | .NET SDK 6.0 / 7.0 / 8.0 / 9.0 중 아무거나 (.NET Framework 4.7.2 빌드 지원) |
| 교보문고 전자도서관 | `C:\Program Files (x86)\Kyobobook\eLibrary\` 설치 (경로 다르면 [run.bat](run.bat) 상단 수정) |

---

## 🔨 빌드

```bash
dotnet build KyoboDumper\KyoboDumper.csproj -c Release
```

빌드 결과물: `KyoboDumper\bin\Release\KyoboDumper.dll`

---

## 🚀 사용법

1. **본인이 대출·구매한 책을 교보문고 전자도서관 앱에서 미리 다운로드** (서재에 다운로드 완료 상태여야 함)
2. 저장소 루트에서 `run.bat` 더블클릭 (관리자 권한 프롬프트 승인 시 훅 DLL 자동 배포 후 뷰어 실행)
3. 뷰어가 뜨면 **서재에서 읽을 책을 클릭하여 열기**
4. 페이지를 넘기지 않아도 1~2초 뒤 `dump\` 폴더에 책 형식에 맞는 완성본이 자동 저장됨:
   - **PDF**: `dump\<도서명>.pdf`
   - **EPUB**: `dump\<도서명>.epub` (및 `dump\<도서명>_epub\` 구조 백업 폴더)
   - **Comic**: `dump\<도서명>.cbz` (및 `dump\<도서명>_comic\` 원본 고화질 이미지 폴더)

성공 시 로그(`dump\dumper.log`) 예시:

```
[BookOpen] >>> USER OPENED BOOK: "귀멸의 칼날. 1" | Type=zip
[ZipExtract] Extracted to: C:\Users\...\ELibrary\B2C\<uuid>
[Comic] Found KyoboDRM key: Key=k7K80x0OL3194880, IV=nJo75t33sX6E7USe. Decrypting all pages in one shot...
[Dump] [SUCCESS] Packaged complete Comic in ONE SHOT: ...\dump\귀멸의_칼날.cbz (194 pages, 279361238 bytes)
```

EPUB 성공 로그 예시:
```
[BookOpen] >>> USER OPENED BOOK: "재벌가 망나니는 SSS급 연금술사였다. 1" | Type=epub
[ZipExtract] Extracted to: C:\Users\...\ELibrary\B2C\<uuid>
[EPUB] Proactively decrypted Fasoo file: chapter1.xhtml (62512 bytes)
[EPUB] Total 8 Fasoo chapter(s) decrypted in-place!
[Dump] [SUCCESS] Packaged clean EPUB: ...\dump\재벌가_망나니는_SSS급_연금술사였다.epub (1607547 bytes)
```

---

## 🧭 작동 원리 (요약)

```
run.bat (관리자 권한으로 DLL 복사 후 APPDOMAIN_MANAGER 환경변수 설정)
   ↓
KyoboBook.Ebook.ELibrary.exe 시작 (.NET Framework CLR이 KyoboDumper.dll 로드)
   ↓
HookAppDomainManager.InitializeNewDomain()
   ├─ 0Harmony.dll 사이드로드 (AssemblyResolve)
   ├─ KyoboBook.Ebook.Controls 로드 감지:
   │     └─ LibraryPresenter.OpenMedia() Prefix 후킹 → 현재 열린 도서 제목/타입 캐치
   ├─ KyoboBook.Ebook.Platform 로드 감지:
   │     ├─ Helper.Zip.Extract() Postfix 후킹 → 도서 압축 해제 감지 & 원샷 덤프 트리거
   │     ├─ Helper.IOHelper.DirectoryDelete() Prefix 후킹 → 임시 폴더 삭제 직전 최종 백업
   │     └─ DrmService.OpenSecondDrmContent() 후킹 → Fasoo 챕터 복호화 연동
   └─ KyoboBook.Ebook.Platform.Container 로드 감지:
         ├─ MediaSource.Extract() / ExtractFilePath() Postfix 후킹 → PDF 파일 캐치
         └─ eval_w / eval_v.ExtractFilePath() 후킹
   ↓
[도서 클릭 시 분기 동작]
   ├─ [PDF]  : MediaSource.Extract()가 반환한 byte[] 가 %PDF 시그니처 검증 후 즉시 저장
   ├─ [EPUB] : Zip.Extract 완료 즉시 내부 *.xhtml 의 Fasoo DRM(\x9b DRMONE)을
   │           DrmService 로 일괄 복호화 후 IDPF 표준 규격 <도서명>.epub 으로 압축 패키징
   └─ [Comic]: Zip.Extract 완료 즉시 info.xhtml 에서 AES 키/IV 추출 →
               img/ 폴더 내 전 페이지를 AES-128-CBC 복호화(저화질 thumb 폴더 제외) →
               194장 전 페이지를 순서대로 <도서명>.cbz 로 압축 패키징
```

---

## 🗂 프로젝트 구조

```
KyoboPDFDumper_AGY/
├── KyoboDumper/
│   ├── KyoboDumper.csproj    # .NET Framework 4.7.2 클래스 라이브러리
│   ├── StartupHook.cs         # AppDomainManager CLR 진입점 & 어셈블리 리졸버
│   └── PdfPatches.cs          # Harmony 후킹, Fasoo/AES 복호화, EPUB/CBZ 패키징
├── dump/                      # 덤프 결과물 자동 저장 디렉토리 (dumper.log 포함)
├── run.bat                    # 환경변수 세팅 후 뷰어 실행 스크립트
├── LICENSE                    # MIT
└── README.md
```

---

## 🧪 안 될 때 진단

| 증상 | 원인 / 조치 |
| --- | --- |
| `Hook DLL not found` | `dotnet build KyoboDumper\KyoboDumper.csproj -c Release` 먼저 실행 |
| `EXE not found` | [run.bat](run.bat) 상단 `KYOBO_INSTALL_DIR` 경로가 실제 교보 뷰어 설치 경로와 맞는지 확인 |
| 뷰어는 켜지는데 로그에 `[KyoboDumper]` 안 뜸 | .NET Framework `APPDOMAIN_MANAGER` 설정이 적용되지 않음. `run.bat`을 관리자 권한으로 실행했는지 확인 |
| 책을 열었는데도 `dump\` 에 파일이 안 생김 | 도서가 완전히 다운로드되지 않은 상태에서 열었거나, 뷰어 서재 목록 갱신 필요. 서재에서 도서 다운로드 완료 후 다시 클릭 |
| EPUB 파일 본문이 깨지거나 안 열림 | `dumper.log`에 `[EPUB] Proactively decrypted Fasoo file` 로그가 찍혔는지 확인. 책을 뷰어로 완전히 열어본 뒤 뷰어를 닫으면 닫히는 순간 재패키징됨 |
| 만화 페이지 순서가 뒤섞임 | 파일명이 숫자가 아닌 특수 포맷인 경우. `dump\<도서명>_comic\` 원본 폴더에서 수동 정렬 확인 |

### ⚠️ 사용 시 유의

- 이 도구는 **본인이 정당하게 구매·대출한 콘텐츠** 를 개인 백업·연구·호환 기기 이전 등의 목적으로 사용하는 것을 전제로 합니다.
- 추출한 파일의 **재배포·업로드·공유는 저작권법 위반** 이며, 이 도구 배포자와 무관합니다.
- 사용자는 자신이 속한 국가/지역의 저작권법 및 교보문고 이용약관을 준수할 책임이 있습니다.
- 도구 사용으로 발생하는 어떤 법적·기술적 결과에도 개발자는 책임지지 않습니다.

### AI

- Gemini 3.8 Flash(High) + Claude Opus 4.7(Medium)

---

## 📜 License

MIT License — 자세한 내용은 [LICENSE](LICENSE) 참고.
