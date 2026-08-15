# Advanced File Recovery

A native Windows desktop file-recovery tool (WPF, .NET 8) with a real, working
recovery engine: NTFS `$MFT` parsing, FAT32/exFAT directory parsing, and
sector-by-sector raw file carving by signature — no mock data.

## Why WPF over WinUI 3

WPF was chosen over WinUI 3 for this project because:
- Mature, stable Win32 interop story for `CreateFile`/`DeviceIoControl` raw disk access.
- `dotnet publish -p:PublishSingleFile=true` produces a genuinely portable single
  `.exe` with WPF; WinUI 3's packaging story (MSIX-first) is heavier for a tool
  people need to grab and run immediately during a data-loss emergency.
- No dependency on the Windows App SDK runtime being present on the user's machine.

## Project layout

```
FileRecoveryPro.sln
src/
  FileRecovery.Core/           Class library — the actual recovery engine, no UI
    Disk/                      Raw device access (P/Invoke), volume enumeration
    FileSystems/                NTFS $MFT, FAT32, exFAT parsers
    Carving/                    Signature catalogue + sector-by-sector carver (Deep Scan)
    Recovery/                   Orchestration, destination-safety gate, preview reader
    Models/                     Shared data models
  FileRecovery.App/             WPF UI (MVVM via CommunityToolkit.Mvvm)
    app.manifest                 Forces UAC elevation (requireAdministrator)
.github/workflows/build.yml     CI: builds + publishes a portable win-x64 EXE
```

## How the recovery engine actually works

### Read-only raw disk access (`Disk/RawDiskReader.cs`)
Opens `\\.\PhysicalDriveN` or `\\.\D:` via `CreateFile` with **only**
`GENERIC_READ`. Nothing in the codebase ever requests `GENERIC_WRITE` against
a source device, and there's exactly one class permitted to touch a device
path at all — every parser and the carver read through it.

### Quick Scan
- **NTFS** (`FileSystems/NtfsMftParser.cs`): locates the `$MFT` from the boot
  sector, walks `FILE` records, applies the NTFS "update sequence array"
  fixup, and looks for records with the `IN_USE` flag cleared. Parses
  `$FILE_NAME` and non-resident `$DATA` attributes (including decoding the
  compressed data-run list to get physical cluster locations). Cross-references
  `$Bitmap` (record 6) to flag whether a deleted file's clusters have already
  been reallocated.
- **FAT32 / exFAT** (`Fat32Parser.cs`, `ExFatParser.cs`): parses the real boot
  sector/BPB, walks directory clusters, finds entries marked deleted (`0xE5`
  for FAT32, the in-use bit cleared on the entry-set head for exFAT),
  reconstructs long file names from LFN / File-Name sub-entries, and builds a
  best-effort cluster run for recovery (FAT-deleted files' chains are
  typically zeroed on delete, so — per standard undelete practice — the
  parser assumes contiguous allocation sized from the directory entry).

### Deep Scan (`Carving/SignatureCarver.cs`)
Streams the raw device in 8 MB windows (with inter-chunk overlap so a
signature spanning a chunk boundary isn't missed) and matches header magic
bytes for JPEG, PNG, PDF, ZIP/OOXML (DOCX/XLSX/PPTX), MP4/MOV, MP3, and RAR.
Where the format encodes its own length, the carver **walks the real
structure** instead of grabbing a fixed size:
- PNG: walks chunks to `IEND`.
- ZIP/OOXML: walks local file headers to the End Of Central Directory record.
- MP4: sums ISO-BMFF box sizes.
- MP3: reads the ID3v2 synchsafe tag size.
- JPEG/PDF: scans forward for the format's footer marker.

### Recovery (`Recovery/RecoveryEngine.cs`)
Copies bytes straight from the raw source handle to the destination file
using the cluster runs / carve range discovered during scanning — 4 MB
buffered reads, no filesystem-level "open by path" on the source (which
wouldn't work for a deleted file anyway).

### Destination safety (`Recovery/DestinationSafety.cs`)
Before any bytes are written, the engine resolves both the source device and
the destination folder's drive letter to their underlying **physical disk
index** via WMI (`Win32_DiskDrive` / `Win32_DiskPartition` /
`Win32_LogicalDisk` associations) and refuses to proceed if they match. This
is a hard `InvalidOperationException`, not just a UI hint — it fires even if
someone drives the engine directly instead of through the wizard.

## UAC elevation

`app.manifest` sets `requestedExecutionLevel level="requireAdministrator"`,
so Windows shows the UAC prompt at launch. `App.xaml.cs` also double-checks
`WindowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator)` on startup and
shows a clear message + exits if elevation wasn't granted, rather than
failing confusingly deep inside disk I/O.

## Building

Requires the .NET 8 SDK on Windows (WPF only builds on Windows).

```powershell
dotnet restore
dotnet build -c Release
```

### Portable single-file EXE

```powershell
dotnet publish src/FileRecovery.App/FileRecovery.App.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

Produces `publish/AdvancedFileRecovery.exe` — a single portable executable
(no installer required). CI does this automatically on every push via
`.github/workflows/build.yml` and uploads it as a workflow artifact.

## Known limitations / next steps

This is a genuine, working engine, not a toy — but a few things are called
out honestly rather than glossed over, so you know where to invest next:

- **NTFS path reconstruction**: `$FILE_NAME`'s parent MFT reference is parsed
  but full original-path reconstruction (walking parent records back to the
  volume root) isn't wired up yet — recovered NTFS entries currently show
  filename only. The parent ref is captured and ready to use.
- **FAT32/exFAT recovery confidence**: because a deleted FAT entry's cluster
  chain is usually zeroed by Windows, the parsers assume contiguous
  allocation sized from the directory entry's file-size field. This is
  standard undelete practice and works for the common case (small-to-medium,
  unfragmented files) but can under-recover heavily fragmented files.
- **exFAT directory chains**: the exFAT walker reads the first cluster of
  each directory; very large deleted-heavy directories that span multiple
  clusters would need the FAT chain walked too (straightforward addition,
  not yet wired up).
- **RAR carving** uses a fixed size cap rather than parsing RAR block
  structure (RAR5's block format is more involved); a follow-up could parse
  RAR's own headers the way the ZIP/PNG/MP4 carvers do.
- **ReFS** is called out in the UI's `FileSystemKind` enum but no ReFS parser
  is implemented yet — ReFS's on-disk format is not publicly documented by
  Microsoft to the same degree as NTFS, so Deep Scan (signature carving,
  which is filesystem-agnostic) is the practical path for ReFS volumes today.
- Add code signing before wide distribution — Windows SmartScreen will warn
  on an unsigned EXE that requests admin rights and touches raw disks.
