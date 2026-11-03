# 🏝️ PulseIsland

A minimal, transparent, Dynamic Island-style system monitor widget for Windows 11. Built specifically for modern laptops like the **ASUS Vivobook S15 Evo (Intel Core i5-12500H hybrid P/E cores, Intel Iris Xe graphics)** to provide real-time CPU temperatures, fan RPM, and live hardware telemetry that Windows Task Manager leaves out.

Target idle footprint: **< 1% CPU** and **< 60 MB RAM**.

---

## ✨ Features & Visual Polish (iOS Level)

- **Continuous-Curvature Squircle (Superellipse)**:
  - Custom $G^2$ continuous-curvature geometry via cubic Bézier spline calculation instead of standard circular `CornerRadius`.
  - **Island Mode (Default)**: Floats $8\text{ px}$ below top edge with full-height continuous curvature rounding.
  - **Notch Mode**: Flush to the screen top edge ($Y=0$) with a flat top and smooth squircle curvature on bottom corners.
  - Pure black (`#000000`) fill at $\approx 96\%$ opacity with an ultra-subtle $0.5\text{ px}$ inner stroke at $8\%$ white (`#14FFFFFF`).
  - Soft large shadow (blur $24$, $25\%$ black, offset-Y $6$) rendered inside an outer transparent window margin—guaranteed **zero gray boxes, halos, or rectangular artifacts** even over bright wallpapers.

- **Collapsed Pill State ($\approx 300 \times 44\text{ DIPs}$)**:
  - Monoline vector Thermometer icon + CPU temperature on the left.
  - Monoline vector Fan icon + RPM on the right.
  - No redundant "CPU" text label.
  - Smooth temperature color interpolation:
    - 🟢 `#30D158` ($<60^\circ\text{C}$, iOS Green)
    - 🟡 `#FFD60A` ($60^\circ\text{C} - 75^\circ\text{C}$, iOS Yellow)
    - 🟠 `#FF9F0A` ($75^\circ\text{C} - 85^\circ\text{C}$, iOS Orange)
    - 🔴 `#FF453A` ($>85^\circ\text{C}$, iOS Red)
  - Number roll transitions: Numeric values animate smoothly over $200\text{ ms}$, never snapping abruptly.
  - Segoe UI Variable Display with tabular figures (`Typography.NumeralAlignment="Tabular"`).

- **Damped-Spring Motion**:
  - Damped spring easing ($\zeta \approx 0.8$, $350\text{ ms}$, natural subtle overshoot) anchored at the top-center axis.
  - Micro-interaction: Tiny scale anticipation ($1.03\times$) on hover start before spring expansion.
  - Expanding reveals rounded tiles (`#1C1C1E`, $16\text{ px}$ radius) with a $30\text{ ms}$ staggered entrance.
  - Collapsing fades out content first ($100\text{ ms}$) before shrinking the squircle.
  - **Thermal Throttle Warning**: Slow breathing red edge glow ($1.6\text{ s}$ loop) when CPU throttles or exceeds $95^\circ\text{C}$.

- **Expanded Card State**:
  - **CPU Tile**: Live temp + round-cap load progress ring.
  - **Cooling Fan Tile**: Fan RPM and status from ASUS ACPI.
  - **GPU & Memory Tile**: Intel Iris Xe load and physical RAM used/total.
  - **Battery & Power Tile**: Battery charge % and charge/discharge wattage rate.
  - **Network Speed Tile**: Live download and upload throughput.
  - **Per-Core Badges**: Compact row for Intel 12th Gen hybrid architecture (P-Cores `P1`–`P4` and E-Cores `E1`–`E8`).
  - **60-Second Sparkline**: Real-time CPU temperature history with a soft gradient area fill.

- **DPI-Correct Centering & Window Controls**:
  - Native monitor bounds queried via Win32 `MonitorFromWindow` and converted to Device Independent Pixels (DIPs) via `CompositionTarget.TransformFromDevice`.
  - Position locked to top center by default (stale saved positions automatically reset).
  - Unlocking allows horizontal repositioning.
  - Global click-through shortcut: <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>I</kbd>.
  - Auto-hide in fullscreen games and applications.

---

## 🛠️ ASUS ACPI Fan Reading

ASUS Vivobook laptops do not expose fan speeds to standard SuperIO or EC registers queried by LibreHardwareMonitor. PulseIsland features a dedicated fallback:
1. Queries LibreHardwareMonitor fan sensors.
2. If unavailable, directly opens `\\.\ATKACPI` via `CreateFile` and issues `DeviceIoControl` with IOCTL `0x0022240C` using the ASUS `DSTS` method (`0x53545344`) with CPU fan device ID `0x00110013` (and GPU fan `0x00110014`).
3. Decodes the returned value: `(raw & 0xFFFF) * 100` RPM.
4. **Display Rules**:
   - $\text{RPM} > 0$: `1800 rpm`
   - $\text{RPM} == 0$: `fan off`
   - Unavailable: `fan --`

---

## 🚀 Build & Run

### 1. Run Direct Standalone Executable
The single-file executable is located at:
```
./publish/PulseIsland.exe
```
Simply double-click or run from PowerShell:
```powershell
.\publish\PulseIsland.exe
```

### 2. Hardware Diagnostics (`--diag`)
To inspect display DPI scaling, centering calculations, and all detected hardware sensors:
```powershell
.\publish\PulseIsland.exe --diag
```
This prints directly to the terminal, and saves the output to:
- `diag.txt` (in current working directory and next to `.exe`)
- `%AppData%\PulseIsland\sensors_debug.log`

### 3. ASUS Fan Telemetry Probe (`--fan-probe`)
To scan device IDs `0x00110010` through `0x00110035` and check `root\wmi\AsusAtkWmi_WMNB`:
```powershell
.\publish\PulseIsland.exe --fan-probe
```
Saves report to:
- `fan_probe.txt` (in current working directory and next to `.exe`)
- `%AppData%\PulseIsland\fan_probe.txt`

### 4. Rebuild from Source
```powershell
dotnet build PulseIsland.csproj
dotnet publish PulseIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

---

## ⌨️ Shortcuts & Context Menu

| Option | Description |
| :--- | :--- |
| **Draggable Anywhere** | Click and hold anywhere on the island (4 px threshold) to drag freely in X and Y across monitors. Snaps within 12 px of center or top edge. |
| **Double-Click** | Double-click the pill anytime to reset to top-center. |
| **Lock Position** | Right-click menu toggle to prevent accidental dragging. Remembers position offset per monitor. |
| **Reset to Center** | Recalculates and snaps the widget to top-center in DIPs. |
| **Mode: Island** | Floats 8 px below top edge with full squircle rounding. |
| **Mode: Notch** | Flush to top edge with flat top and rounded bottom corners. |
| **Toggle Visibility** | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>P</kbd> instantly hides or reveals the island. |
| **Click-Through Mode** | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>I</kbd> toggles `WS_EX_TRANSPARENT`. |
| **Pin on Top** | Toggles window topmost state. |
| **Auto-Hide in Fullscreen** | Automatically hides and pauses updates when playing games or running fullscreen apps. |
| **Hardware Diagnostics Log...** | Opens the active sensor diagnostic log file. |

---

## 🧪 Self-Test Mode (`--selftest`)
To verify every live metric (CPU load & temp, 12-core temps, Iris Xe GPU load & shared memory, physical RAM, battery status, network throughput, and fan RPM) with a 15-second real hardware diagnostic and 8-second multi-core CPU burn:
```powershell
.\publish\PulseIsland.exe --selftest
```
This logs live samples every 500 ms, validates min/max variation, and generates:
- `selftest.txt` (in current working directory and next to `.exe`)
- `%AppData%\PulseIsland\selftest.txt`
