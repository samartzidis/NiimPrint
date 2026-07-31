# NiimPrint

A native Windows CLI for Niimbot BLE label printers.

## Requirements

- Windows 10 2004 (build 19041) or later, or Windows 11
- A Niimbot printer paired/reachable over Bluetooth LE

## Supported models

`b1`, `b18`, `b21`, `d11`, `d11_h`, `d110`

Models and their specs (max head width, max density, DPI) are read from
`NiimPrint.json` (named after the exe) next to the exe. To add or adjust a model,
edit a printer entry, e.g.:

```json
{ "Name": "d61", "MaxHeadWidthPx": 384, "MaxDensity": 5, "Dpi": 203 }
```

The values should come from the printer's real datasheet
(head width/DPI mistakes will cause it to reject or misprint valid label
lengths).

## Usage

```
niimprint <command> [options]
```

### `canvas` — generate a correctly-sized blank label

```
niimprint canvas <name> <length> [-m <model>]
```

Creates a landscape PNG (`<name>.png`) sized for the label length (mm) and the
model's fixed head width, ready to open in an image editor.

```
niimprint canvas mylabel 30 -m d110
```

### `print` — print an image

```
niimprint print <image> [options]
```

| Option | Default | Description |
|---|---|---|
| `-m, --model` | `d110` | Printer model |
| `-d, --density` | `3` | Print density (1-5, capped per model) |
| `-n, --quantity` | `1` | Print quantity |
| `-r, --rotate` | `0` | Rotate clockwise: `0`, `90`, `180`, `270` |
| `-t, --threshold` | `128` | Black/white cutoff (0-255) for converting to 1-bit |
| `--dither` | off | Use Floyd-Steinberg dithering instead of a flat threshold |
| `--vo`, `--ho` | `0` | Vertical/horizontal offset in pixels |
| `-v, --verbose` | `0` | `0`/`1`=info, `2`=debug, `3`=trace |

A landscape image (e.g. from `canvas`) that doesn't fit the head width as-is is
auto-rotated 90° before printing. Dithering is off by default — if your artwork
needs it (e.g. a photo), either pass `--dither` or dither the relevant region
yourself in an image editor before printing, which gives finer control than a
whole-image pass.

```
niimprint print mylabel.png -m d110 -d 3
```

### `info` — show printer info

```
niimprint info [-m <model>]
```

Prints device serial, software/hardware version, and battery level.


