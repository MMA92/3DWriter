# 3DWriter

Use your 3D printer with a pen to write letters, birthday cards, etc.

> **This is a fork** of [boy1dr/3DWriter](https://github.com/boy1dr/3DWriter), heavily inspired by the original project. The main change is a port from the original WinForms/.NET Framework app to **modern .NET 8**, making it fully cross-platform — it now runs natively on **Linux and Mac**, not just Windows. Also I had added support for German Umlauts.

---

## Cross-platform version (Linux/Mac/Windows)

A .NET 8 port lives in `3DWriterCore` / `3DWriterCli` / `3DWriterGui` (no WinForms/WPF — runs anywhere .NET 8 does, including Linux and Mac).

### Which command do I run?

Pick **one row** depending on your situation — you don't run all of these in sequence, just the one that matches what you want to do.

| Goal | Have `dotnet` installed | No `dotnet` installed (use Podman/Docker) |
|---|---|---|
| Just try the GUI | `dotnet run --project 3DWriterGui` | `podman run --rm -v "$PWD":/src:Z -w /src/3DWriterGui mcr.microsoft.com/dotnet/sdk:8.0 dotnet build` |
| Generate GCode via CLI | `dotnet run --project 3DWriterCli -- --text "Hello" --font futural --out out.gcode` | `podman run --rm -v "$PWD":/src:Z -w /src/3DWriterCli mcr.microsoft.com/dotnet/sdk:8.0 dotnet run -- --text "Hello" --font futural --out /src/dist/out.gcode` |
| See all CLI flags | `dotnet run --project 3DWriterCli -- --help` | same, run inside the container |
| Build standalone GUI binary | `dotnet publish 3DWriterGui -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist-gui` | `podman run --rm -v "$PWD":/src:Z -w /src/3DWriterGui mcr.microsoft.com/dotnet/sdk:8.0 dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /src/dist-gui` |
| Build standalone CLI binary | `dotnet publish 3DWriterCli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist` | `podman run --rm -v "$PWD":/src:Z -w /src/3DWriterCli mcr.microsoft.com/dotnet/sdk:8.0 dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /src/dist` |
| Run the published GUI binary | `./dist-gui/3DWriterGui` (no `dotnet` needed) | same |

**Note:** the published binary needs its `fonts/` folder next to it (copied automatically into the output dir) — keep them together if you move it.

### Cleaning up after a Podman build

Podman runs as root inside the container, so the `bin`/`obj` folders it creates can end up root-owned. They're gitignored but still clutter `git status`. Run this once after building with Podman:

```sh
rm -rf 3DWriterCli/bin 3DWriterCli/obj 3DWriterCore/bin 3DWriterCore/obj \
       3DWriterGui/bin 3DWriterGui/obj 3DWriterGui/.avalonia-build-tasks
```

---

## How to use it

The GUI has three main columns:

- **Text entry**
- **Preview**
- **GCode settings**

Steps:

1. Pick a font from the toolstrip at the top.
2. Click **Preview** (next to the font selector) to see the rendered character set. The **"Simple fonts"** checkbox toggles between the full font list and a simplified one.
3. Type your text into the text input.
4. Choose a scale (0.2 is close to natural handwriting).
5. Click **Preview** again to render your text in the preview window. You can scale up the preview rendering just to see it more clearly (this has no effect on the actual GCode).
6. Once your GCode settings are complete, click **Generate GCode** and save the file for your printer.

Note: fonts in other languages likely won't map correctly, since the character set is English-only.

---

## GCode settings

*Pay close attention here — these directly affect print quality and safety.*

| Setting | Meaning |
|---|---|
| Bed X / Bed Y | Size of your printer's bed |
| Offset X / Offset Y | Offset if your pen is mounted off-center (e.g. strapped to the side of the extruder) |
| Pen up / Pen down | Height of the extruder/pen tip above the build plate — test manually on your printer |
| Travel / Draw speed | Movement speed; slower is generally better for stability |
| Line Spacing | Gap between lines, measured in "units" |
| Letter Spacing | Extra gap between letters (on top of each letter's built-in spacing) |
| Home X/Y/Z | Whether to home each axis before/after writing — untick any you don't want homed |
| Dry run | Pen never touches down — useful for checking paper/card alignment before committing |

Preview magnification is just a visual aid and doesn't affect the GCode output, which is rendered at higher resolution than what you see in the preview window.

---

## What is a "unit"?

Fonts are described as sets of x/y integer points, with each font roughly 37 units tall. This isn't mm or pixels — it's just the coordinate system the font strokes were originally defined in. The height gets multiplied by your chosen scale to render the font, and line/letter spacing are scaled the same way, so everything stays proportional.

---

## Setting up your printer as a plotter

- A rubber band to hold the pen wiggled too much, so a custom 3D-printed holder was made that attaches firmly to the extruder, with a mating piece glued to the pen (a 4-colour Bic pen in this case).
- The pen tip, when retracted, sits higher than the nozzle, so it can stay mounted permanently.
- Use a tool like Pronterface to manually jog the extruder and find the correct pen-up/pen-down height, plus the X/Y offset so writing doesn't go off the edge of the page.

---

## Laser attachment

The printer was also equipped with a cheap 1W UV laser from eBay, which works well on a variety of materials.

- **Marlin firmware** has no native laser control — the workaround is wiring the laser to the cooling fan output and using `M106`/`M107` to turn it on/off.
- **Repetier firmware** has full laser support. It was flashed onto a GT2560 controller, using the mosfet output normally reserved for a second extruder as the 12V power source for the laser. Repetier's online configuration tool made setting the control output straightforward.
- A cheap 1W laser typically needs 5V — an **LM7805 regulator** steps the 12V supply down. A physical switch was also added so the laser can only fire when intended.

### Firmware-specific notes

**Repetier users:** remove any manual laser on/off commands — 3DWriter inserts the required GCode automatically.

**Marlin users:** there's no native laser GCode support, so you must configure the laser ON/OFF commands yourself in the program. If you haven't wired a laser yet, you can add a switch to your part-cooling fan wiring to toggle between cooling and laser mode:
- Laser ON: `M106`
- Laser OFF: `M107`

**All users:** cheap sub-$25 1W eBay lasers usually need 5V — use an appropriate supply like an LM7805 (cheap, simple to wire).

### Focusing

All lasers need focusing. In the reference setup, focus was found at 68mm from the build plate — this will differ for every setup depending on mounting. Recommended approach: put thick cardboard on the build plate to avoid damage, print simple test text via Repetier Host or Pronterface, adjust Z-height, and repeat until results look sharp.

---

## Use at your own risk

Tested and confirmed working on a RepRap i3 clone. Other software for Ultimaker printers has been known to have inverted Z-axis moves — **simulate GCode files before printing** to be sure of what will happen, and as always with new 3D-printer software, keep one hand on the off switch.

---

## Final notes

Not claiming to be the world's best programmer or a certified 3D-printer expert, but there's years of hands-on experience behind this. The C# 2015 project is included so you can compile it yourself, along with a Windows binary build (`3DWriter/bin/Release`). Feedback welcome.

---

## Changelog

| Date / Version | Changes |
|---|---|
| 13/04/2018 — Blocky text | Fixed blocky/8-bit look some users saw; fixed commas appearing instead of periods for decimals in GCode (localization issue) |
| 13/04/2018 — Update checking | More reliable update-check method added |
| 12/01/2017 — v1.1 | Added real line height to the status bar |
| 21/01/2017 — v1.2 | Replaced hardcoded path separators with `Path.DirectorySeparatorChar`; started work on a font editor (included but disabled in the menu, not yet complete) |
| 24/01/2017 — v1.3 | Fixed GCode Y-axis offset issue; fixed incorrect character array indexing causing erroneous moves; added version checking; changed UI font scale to font size in mm |
