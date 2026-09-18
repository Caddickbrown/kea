# Kea on macOS and Linux

The original Kea is a .NET Framework 4.6.1 Windows Forms application. Every layer of that stack is
Windows-only, so this is a port rather than a recompile: the logic was moved to .NET 8 and each
Windows-specific dependency swapped for a cross-platform one.

The original `Kea/` project is untouched, and `Kea.sln` still opens in Visual Studio exactly as
before. The port lives alongside it in `src/` and has its own solution, `Kea.CrossPlatform.sln`.

## Layout

| Project | What it is | Runs on |
| --- | --- | --- |
| `src/Kea.Core` | All scraping, downloading and packaging logic. No UI. | Windows, macOS, Linux |
| `src/Kea.Cli` | Console front-end (`kea`). No desktop session needed. | Windows, macOS, Linux, headless |
| `src/Kea.Gui` | Avalonia desktop app, the GUI equivalent of the original. | Windows, macOS, Linux |
| `tests/Kea.Core.Tests` | xUnit tests for the logic and the output formats. | Windows, macOS, Linux |

Both front-ends call the same `Kea.Core`. The original kept its logic inside the WinForms form,
which is what tied it to Windows in the first place.

## Requirements

The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else — no Visual
Studio, no Mono, no native image libraries.

```bash
# macOS
brew install --cask dotnet-sdk

# Debian / Ubuntu
sudo apt-get install -y dotnet-sdk-8.0
```

## Build and test

```bash
git clone https://github.com/RustingRobot/Kea.git
cd Kea
dotnet build Kea.CrossPlatform.sln
dotnet test Kea.CrossPlatform.sln
```

## Run the GUI

```bash
dotnet run --project src/Kea.Gui
```

It behaves like the original: paste comic list links, add them to the queue, set a start and end
chapter per comic, pick a save folder and a format, press **start**.

To produce a standalone build that does not need the SDK installed:

```bash
# Apple silicon
dotnet publish src/Kea.Gui -c Release -r osx-arm64 --self-contained

# Intel Macs
dotnet publish src/Kea.Gui -c Release -r osx-x64 --self-contained

# Linux
dotnet publish src/Kea.Gui -c Release -r linux-x64 --self-contained
```

On Linux the GUI needs a desktop session (X11 or Wayland). On a headless box, use the CLI.

## Run the CLI

```bash
dotnet run --project src/Kea.Cli -- --help

# one comic, as PDFs, into ~/Comics
dotnet run --project src/Kea.Cli -- \
  "https://www.webtoons.com/en/action/a-comic/list?title_no=1234" -o ~/Comics

# a queue from a file, as CBZ, chapters 10 to 20, one folder per comic
dotnet run --project src/Kea.Cli -- --urls-from queue.txt -f cbz -s 10 -e 20 --comic-folders
```

`--urls-from -` reads the queue from stdin. Ctrl+C stops cleanly rather than killing the process
mid-file.

## What was replaced, and why

| Original | Problem off Windows | Replacement |
| --- | --- | --- |
| Windows Forms | Windows-only even on .NET 8 | [Avalonia 11](https://avaloniaui.net) |
| `System.Drawing` (`Bitmap`, `Graphics`) | Throws on macOS and Linux from .NET 6 onwards | [ImageSharp 2.1](https://github.com/SixLabors/ImageSharp) (Apache-2.0, matching this repo's MIT licence) |
| iTextSharp 5 | .NET Framework only | A small built-in PDF writer, see below |
| `user32.dll` P/Invoke for window dragging | No `user32.dll` outside Windows | Dropped — the window uses normal decorations |
| `CreateParams` / `CS_DROPSHADOW` | Win32 window class flag | Dropped, the platform draws its own shadows |
| `Process.Start(url)` | Needs `UseShellExecute`; no shell handler on macOS or Linux | `PlatformLauncher`, which uses `open` and `xdg-open` |
| `OpenFileDialog` with a dummy filename | A folder-picker workaround for a WinForms gap | Avalonia's `StorageProvider.OpenFolderPickerAsync` |
| `WebClient` | Obsolete, and shared mutable headers across requests | `HttpClient` with per-request headers |
| `MessageBox.Show` | Part of Windows Forms | A small Avalonia dialog |
| `savepathTB.Text.Contains('\\')` as path validation | No backslashes in POSIX paths, so every path looked invalid | `Directory.Exists` |
| `savePath + "\\" + name` throughout | Backslash is a legal filename character on macOS and Linux | `Path.Combine` |

### The PDF writer

Rather than swap iTextSharp for another PDF library, `ImagePdfWriter` writes the small subset of
PDF needed for one-image-per-page documents, embedding the downloaded JPEG bytes through the
`DCTDecode` filter. That keeps the dependency list short, sidesteps the licence questions around
the current PDF libraries, and — because nothing is decoded and re-encoded — the PDF pages are
bit-for-bit the images the site served. Anything that is not a baseline grayscale or RGB JPEG
(a PNG, or a progressive JPEG) is converted first.

## Bugs found while porting

These were all present in the original. They are fixed here.

1. **Chapter names leaked between comics.** `GetChapterAsync` cleared `chapterLinks` at the start of
   each comic but never cleared `chapterNames`, so from the second comic onwards the names list kept
   growing and no longer lined up with the links. Every chapter of every comic after the first was
   saved under the wrong name. The port keeps each chapter's link and name together in one
   `ChapterRef`, so they cannot drift apart.

2. **Chapters with more than ten images were shuffled.** Images were named `Ch1.0`, `Ch1.1` …
   `Ch1.10`, which sorts `Ch1.10` before `Ch1.2`. CBZ and stitched-image output ordered pages by
   that name. Indices are now zero-padded.

3. **CBZ page order was luck, not design.** `ZipFile.CreateFromDirectory` stores entries in
   filesystem enumeration order. NTFS happens to return directory entries alphabetically, so this
   looked correct on Windows; ext4 and APFS make no such promise, so a straight port would have
   produced scrambled archives. Entries are now written explicitly in page order.

4. **Stitched images clipped anything wider than the first image.** The canvas was sized to
   `images[0].Width`. It is now sized to the widest image.

5. **Page order depended on file creation time.** Images were re-read with
   `GetFiles("*.jpg").OrderBy(fi => fi.CreationTime)`. Creation time is unreliable on Linux and
   ties are common on any platform when files are written in the same tick. Order is now carried
   explicitly from the download loop.

6. **Bad links were dropped in silence.** `addToQueueBtn_Click` skipped anything it could not parse
   without a word, so a typo looked identical to a network problem. Both front-ends now say which
   line was rejected and why.

7. **Only `.jpg` was ever assumed.** Every image was saved as `.jpg` regardless of what arrived.
   The extension now comes from the bytes themselves.

## Deliberate behaviour changes

- **The window has normal decorations.** The original drew its own title bar and dragged the window
  through `user32.dll`. Hand-rolled chrome fights macOS traffic lights and Linux tiling window
  managers, so the port uses the platform's own.
- **Filenames are sanitised against all three platforms at once.** The original used
  `Path.GetInvalidFileNameChars()`, which on Linux is only `/` and NUL. A chapter called
  `Ep 3: What?` would have been written verbatim on Linux and then been unreadable on Windows.
  Downloads are meant to be portable, so the stricter Windows rules are applied everywhere.
- **Images download three at a time** instead of strictly one, with `--parallel` to change it.
- **A failing chapter no longer stops the queue.** Failures are collected and reported at the end.
- **Mobile links work.** `m.webtoons.com` is rewritten to `www.webtoons.com` instead of being
  rejected.
- **Existing files are never overwritten**; a numbered suffix is added instead.

## Known limitations

- **The scrapers are unverified against the live site.** The selectors were rewritten to be far
  more tolerant than the original's hard-coded child indices, and they are tested against markup
  of the expected shape, but no request was made to webtoons.com while porting. The README already
  notes the project is unmaintained and that the site has changed since; if a download returns no
  chapters or no images, the selectors in `WebtoonScraper` are the place to look. They are two
  XPath expressions with fallbacks, deliberately kept easy to adjust.
- **`Kea/` still only builds on Windows.** It is left alone on purpose. The CI workflow builds the
  port only.
- **The GUI needs a desktop session.** Use the CLI on headless machines.
