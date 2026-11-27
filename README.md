# RhinoPlugin_ExportImportJWW

A Rhino 8 plug-in that exports layouts and drawings to **Jw_cad (JWW)**. Import is planned.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/JwwExport.cs` | The plug-in (C# 5). It has the `ExportJww` command, a "Jw_cad (*.jww)" file exporter and the JWW writer. |
| `build.ps1` | Builds `bin/JwwExport.rhp`. |
| `python/jww_writer.py` | The same JWW writer in pure Python, for Rhino Python scripts. |
| `tools/jww_read.py` | A JWW reader used to check output. It parses a file and writes a summary to `<file>.jww.txt`. |
| `tools/jww_render.py` | Draws a JWW file to a PNG so you can check it by eye (needs matplotlib). |

## The plug-in

- **`ExportJww` command**
  - Active view is a **layout**: writes the whole sheet. Page-space objects (frame, tables, notes) go out as they are.
    For every parallel detail, the model objects seen through it are clipped to the detail and placed at the detail scale.
  - Active view is a **model view**: writes the selected objects (Enter = all visible) at a scale you type (default 1:100).
  - `-ExportJww` (with the dash) asks for the file path on the command line, for scripts and macros.
- **File > Export / Save As**: the file type list gets **Jw_cad (*.jww)**. It uses the active layout, or the model at 1:100.

### Install

Build it (see below), then drag `bin/JwwExport.rhp` into the Rhino 8 window, or use
**Tools > Options > Plug-ins > Install…**. Rhino loads it again at every start.

### What is written

- JWW data version 6.00. Paper A0–A4 is taken from the layout size. JWW coordinates are paper mm (図寸),
  measured from the paper centre.
- One JWW layer group per drawing scale, named like `1/100`. Rhino layers become JWW layers (16 per group).
- **Lines and curves:** lines, polylines, arcs and circles are written as they are. Other curves (NURBS, ellipses)
  are written as polylines with 0.05 mm chord tolerance.
- **Text:** written in the Rhino font (Shift-JIS). Rhino's alignment is converted to JWW's bottom-left base point.
  Multi-line text becomes one JWW text per line.
- **Dimensions and leaders:** exploded into lines and text.
- **Hatches:** only their boundary lines are written.
- **Blocks:** exploded.
- **Colours:** mapped to the 9 JWW pens. 1–3 are black, thin / normal / thick by print width; 4 is red, 5 blue,
  6 green, 7 magenta, 8 grey and 9 cyan.
- **Line types:** Center / DashDot / Border become 一点鎖線, Hidden / Dashed become 点線, Phantom becomes 二点鎖線.

Not yet included:
- JWW import.
- Solids/fills as JWW ソリッド, real JWW dimensions, JWW blocks and images.
- MPZ (DRA-CAD). The MPZ format is not public.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

You need only the C# compiler that comes with Windows (.NET Framework 4.8, `csc.exe`, C# 5) and Rhino 8's
`RhinoCommon.dll`. No Visual Studio or .NET SDK is needed. The plug-in targets .NET Framework 4.8 and also loads in
Rhino 8 running on .NET Core.

## Checking the output

```powershell
python tools/jww_read.py out.jww          # writes out.jww.txt: version, counts, layers, extents
python tools/jww_render.py out.jww out.png
```

## The JWW format

The binary layout follows LibreCAD's jwwlib (`libraries/jwwlib/src/jwwdoc.cpp`, `jwwdoc.h`). JWW uses MFC CArchive
serialisation, little-endian numbers and Shift-JIS strings with a BYTE length (0xFF + WORD for 255 bytes or more).
The class tag is 0xFFFF + schema + name the first time, then `index | 0x8000`.

The layout was checked by parsing real Jw_cad 7.00 files completely. A 1:50 dimension 4.8 units long carries the
value 240, which confirms that coordinates are paper mm. The output has been opened in DRA-CAD.

## License

MIT. See [LICENSE](LICENSE). The JWW writer is an independent implementation. It only follows the file layout
documented by LibreCAD's jwwlib and contains no jwwlib code.

## Development notes

- Each PC keeps a local clone in `C:\Users\<user>\git\RhinoPlugin_ExportImportJWW`. Do not put the `.git` folder in
  Google Drive.
- The Drive folder `Claude\Projects\002_KURAHA CODE\002_RhinoPlugin_ExportImportJWW` holds only a built `.rhp` and
  notes.
- To test without installing the plug-in, load the assembly in a Rhino Python script with
  `System.Reflection.Assembly.LoadFrom(...)` and call `JwwExport.Exporter(doc).ExportLayout(pageView, path)`.
