# Rhp_JWW_ExportImport

A Rhino 8 plug-in that **exports** layouts and drawings to Jw_cad (**JWW**) and **imports** JWW drawings.
Everything is in one file, `Rhp_JWW_ExportImport.rhp`.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/Plugin/AssemblyInfo.cs` | The plug-in's identity: GUID, name, version. |
| `src/Export/JwwExport.cs` | The `ExportJww` command and the Rhino → JWW conversion. |
| `src/Import/JwwImport.cs` | The plug-in class (a file importer for `.jww`), the `ImportJww` command and the JWW → Rhino conversion. |
| `src/Common/JwwWriter.cs` | The JWW file writer. |
| `src/Common/JwwReader.cs` | The JWW file reader. It covers all object types, blocks and Unicode strings. |
| `build.ps1` | Builds `bin/Rhp_JWW_ExportImport.rhp` and `bin/JwwDump.exe`. |
| `tools/JwwDump.cs` | A console program that prints a summary of JWW files, using the C# reader. |
| `python/jww_writer.py` | The JWW writer in pure Python, for Rhino Python scripts. |
| `tools/jww_read.py`, `tools/jww_render.py` | A Python reader and a PNG renderer for checking files by eye (the renderer needs matplotlib). |

## Install

Build it (see below), then drag `bin/Rhp_JWW_ExportImport.rhp` into the Rhino 8 window, or use
**Tools > Options > Plug-ins > Install…**. Rhino loads it again at every start.

## Export: `ExportJww`

- Active view is a **layout**: writes the whole sheet. Page-space objects (frame, tables, notes) go out as they are.
  For every parallel detail, the model objects seen through it are clipped to the detail and placed at the detail scale.
- Active view is a **model view**: writes the selected objects (Enter = all visible) at a scale you type (default 1:100).
- `-ExportJww` (with the dash) asks for the file path on the command line, for scripts and macros.

What is written:
- JWW data version 6.00. Paper A0–A4 is taken from the layout size. JWW coordinates are paper mm (図寸),
  measured from the paper centre.
- One JWW layer group per drawing scale, named like `1/100`. Rhino layers become JWW layers (16 per group).
- **Lines and curves:** lines, polylines, arcs and circles are written as they are. Other curves (NURBS, ellipses)
  are written as polylines with 0.05 mm chord tolerance.
- **Text:** written in the Rhino font family (Shift-JIS). Rhino's alignment is converted to JWW's bottom-left base
  point. Multi-line text becomes one JWW text per line.
- **Dimensions and leaders:** exploded into lines and text.
- **Hatches:** only their boundary lines are written.
- **Blocks:** exploded.
- **Colours:** mapped to the 9 JWW pens. 1–3 are black, thin / normal / thick by print width; 4 is red, 5 blue,
  6 green, 7 magenta, 8 grey and 9 cyan.
- **Line types:** Center / DashDot / Border become 一点鎖線, Hidden / Dashed become 点線, Phantom becomes 二点鎖線.

Rhino allows only one file type direction per plug-in, so `.jww` is not in File > Export. Use `ExportJww` instead.

## Import: File > Import / Open, or `ImportJww`

- **Size option:**
  - `Real` (the default): JWW paper mm × the layer group's scale, converted to the model unit. A 1/50 group comes in
    at real size.
  - `Paper`: 1:1 paper mm. Groups at different scales then stay together as on the Jw_cad sheet.
- **Layers:** `JWW <file>` › `<group 0–F> <group name>` › `<group><layer 00–FF> <layer name>`. Hidden JWW layers come
  in hidden.
- **Colours and widths:** JWW pen colours (1–9 and SXF colours) and print widths become object colour, print colour
  and print width. JWW line types become Rhino Dashed / Dots / Hidden / Center / DashDot / Phantom / Border, scaled
  to the drawing scale.
- **Objects:**
  - Lines, arcs and circles come in as they are; ellipses and elliptic arcs too (flatness and tilt are kept).
  - Text uses its font and size, with the base point at the bottom-left.
  - Points come in; 仮点 (temporary points) are skipped.
  - ソリッド (solids) become solid hatches.
  - Dimensions become grouped lines and text.
  - Blocks become Rhino block definitions and instances.
- Tested with real Jw_cad 7.00 drawings of 30,000–78,000 objects. Each imports in about a second, and the object
  counts match the file.

Not yet included: JWW images, true Rhino dimensions from JWW dimensions, JWW block attributes. MPZ (DRA-CAD) is not
included because the MPZ format is not public.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

You need only the C# compiler that comes with Windows (.NET Framework 4.8, `csc.exe`, C# 5) and Rhino 8's
`RhinoCommon.dll`. No Visual Studio or .NET SDK is needed. The plug-in targets .NET Framework 4.8 and also loads in
Rhino 8 running on .NET Core.

## Checking files

```powershell
bin\JwwDump.exe some.jww                 # version, paper, scale, object counts, blocks, a few texts
python tools/jww_render.py out.jww out.png
```

## The JWW format

The binary layout follows LibreCAD's jwwlib (`libraries/jwwlib/src/jwwdoc.cpp`, `jwwdoc.h`). JWW uses MFC CArchive
serialisation and little-endian numbers. Strings are Shift-JIS with a BYTE length (0xFF + WORD for 255 bytes or more),
or UTF-16 after the MFC marker 0xFF 0xFFFE. The class tag is 0xFFFF + schema + name the first time, then
`index | 0x8000`; a tag below 0x8000 refers to an object already read.

The drawing list is followed by the block definitions: a count, then for each a `CDataList` and its own list of
members. Version 7 files end with 4 more bytes (the embedded image count).

The layout was checked by parsing real Jw_cad 7.00 files completely. A 1:50 dimension 4.8 units long carries the
value 240, which confirms that coordinates are paper mm. Exported files have been opened in DRA-CAD.

## License

MIT. See [LICENSE](LICENSE). The JWW reader and writer are independent implementations. They follow the file
layout documented by LibreCAD's jwwlib and contain no jwwlib code.

## Development notes

- Each PC keeps a local clone in `C:\Users\<user>\git\Rhp_JWW_ExportImport`. Do not put the `.git` folder in
  Google Drive.
- The Drive folder `Claude\Projects\002_KURAHA CODE\002_Rhp_JWW_ExportImport` holds only a built `.rhp` and notes.
- To test without installing the plug-in, load the assembly in a Rhino Python script with
  `System.Reflection.Assembly.LoadFrom(...)`. Then call `JwwExport.Exporter(doc).ExportLayout(pageView, path)` or
  `JwwImport.Importer(doc).Import(path, paperSize)`.
