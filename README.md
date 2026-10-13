# Temperature Modbus Tester V1.0.0

Windows 用的 Modbus 測試工具（.NET 8 / WPF / MVVM），支援 **Modbus TCP** 與 **Modbus RTU（RS-485）**，
可用 Generic 模式讀寫任何 Modbus 設備，也內建 Siemens **A6V12258321（QFA2050/MO、QFA2050D/MO、QPA2052/MO）**
Device Profile。

| Function | 說明 |
|---|---|
| FC03 Read Holding Registers | Generic / Profile |
| FC04 Read Input Registers | Generic |
| FC06 Write Single Register | Generic（Raw）/ Profile（工程值） |
| FC16 Write Multiple Registers | Generic（Raw 清單） |

---

## 1. 功能

### Generic Mode（Device Profile = Generic Modbus）

- **Read**：Function（FC03 / FC04）、Address、Quantity、Address Base（1-based / 0-based）
- **Data Type**：UInt16（預設，原本的行為）、Int16、UInt32、Int32、Float32（IEEE-754 single）
- **32-bit Order**（只在 32-bit Data Type 啟用）：`ABCD`、`BADC`、`CDAB`、`DCBA`
  （A = 值的最高位 byte，字母順序 = byte 在線上的順序；例：`0x12345678` 的 ABCD = `1234 5678`、CDAB = `5678 1234`）
- **Scale**：Default Scale + 「Apply to all rows」，也可在表格逐列修改；Value = 解碼值 × Scale
- **Write**：FC06（單一 Raw word）、FC16（逗號分隔 Raw words，Quantity = 值的個數，最多 123），
  十進位 `0~65535` 或十六進位 `0x0000~0xFFFF`；寫入後自動 FC03 Verify，並同步表格中已存在的列

### Profile Mode（Device Profile = A6V12258321）

- 依文件解碼（Scale、Signed Int16、Status、Range、Unit）
- **Cumulative read**：Address 代表「讀到這個 Register 為止」，自動合併成連續的 FC03 request（Profile 固定 FC03）
- **System Unit（Reg 401）**：Reg 1 / Reg 223 的 Unit 與 Range 依 401 切換（°C / °F），**Tester 不做 °C↔°F 換算**
- **Software Version**：Reg 1286（Major.Minor）+ Reg 1287（Build），例：`1.00.7`
- **Profile Write**：只用 FC06；工程值 → Raw 驗證（Range、Scale、Status、Excluded value）、確認視窗、FC03 Verify
- **Unit Context**：寫 Reg 223 前必須先讀到 Reg 401（斷線 / 換 Profile 會清除）
- **WriteOnHold**：Reg 764～766（通訊參數）只讀不寫

### 其他

- 每個輸入欄位各自的歷史紀錄（`%LOCALAPPDATA%\ModbusTester\input-history.json`）
- Communication Log：Tx / Rx frame、CRC、Response Time、錯誤分類；**Copy** 與 **Save...**（UTF-8 文字檔，
  預設檔名 `ModbusLog_yyyyMMdd_HHmmss.txt`）。Tester 不會自動寫 log 檔。

---

## 2. 表格欄位（含 32-bit）

| 欄位 | 16-bit | 32-bit（一個值一列，Address = 第一個 Register） |
|---|---|---|
| Raw | Register 值（十進位） | 兩個原始 Register words（線上順序），例 `3F80 0000` |
| Decimal | 有號 / 無號整數（Scale 前） | 32-bit bit pattern 的整數表示（UInt32 / Int32 為其值；Float32 為 bit pattern 的 unsigned 整數） |
| HEX | 4 位 | 依 Order 排好後的 32-bit pattern（8 位） |
| Binary | 16 bit | 32 bit |
| Value | 依 Data Type / Order / Scale 解碼後的工程值 | 同左 |

Float32 範例（ABCD）：

| Raw | Decimal | HEX | Value |
|---|---|---|---|
| `3F80 0000` | `1065353216` | `3F800000` | `1` |

- Float32 的 `NaN` / `+Infinity` / `-Infinity`：照文字顯示，Quality = InvalidValue（不是通訊錯誤）
- Scale 只影響 Value，不影響 Raw / Decimal / HEX / Binary

### Quantity 一律是「16-bit Register 數量」

例：Data Type = Float32、Quantity = 10 → 讀 10 個 Register → 5 個 Float32。
Quantity 為奇數時，最後一個 word 顯示 **Incomplete**（不補 0、不丟棄、不多讀）。

---

## 3. 系統需求與建置

- 執行：Windows 10 / 11 x64（Release package 為 self-contained，**不需安裝 .NET Runtime**）
- 開發：.NET 8 SDK（Windows），Visual Studio 2022 或 `dotnet` CLI

```powershell
dotnet build ModbusTester.slnx                     # Debug
dotnet build ModbusTester.slnx -c Release
dotnet test  tests\ModbusTester.Tests              # Core
dotnet test  tests\ModbusTester.App.Tests          # App（含 WPF UI 與 Simulator TCP 整合測試，見第 5 節）
```

> Visual Studio 或 Tester 正在執行時，預設 `bin` 的 DLL 可能被鎖住（MSB3021 / MSB3027）。
> 這不是編譯錯誤；關閉後再建置，或用 `-o <其他資料夾>` 建置。

---

## 4. Release Package（self-contained win-x64）

```powershell
dotnet publish src\ModbusTester.App\ModbusTester.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o artifacts\ModbusTester_V1.0.0_win-x64
Compress-Archive -Path artifacts\ModbusTester_V1.0.0_win-x64\* -DestinationPath artifacts\ModbusTester_V1.0.0_win-x64.zip -Force
```

- 執行：`artifacts\ModbusTester_V1.0.0_win-x64\ModbusTester.App.exe`（視窗標題 `Modbus Tester V1.0.0`）
- 不使用 SingleFile：保留 DLL / `*.json` / `*.pdb`，方便現場診斷
- `artifacts/` 已列入 `.gitignore`
- Package 不含 Simulator、Python、測試程式
- 版本只在 `src\ModbusTester.App\ModbusTester.App.csproj` 的 `<Version>` 設定一次（標題由 assembly version 取得）

---

## 5. Simulator（開發 / 測試工具，不隨 Release 發佈）

`tools\pi_modbus_server`：pymodbus 3.15 Modbus TCP server，一個 port 依 **Unit ID** 提供兩個裝置：

| Unit ID | 裝置 |
|---|---|
| 1 | **A6V12258321 Simulator**：Reg 1～6、223～225、401、764～766、1286～1287（R / RW 依文件，空隙回 Exception 02） |
| 2 | **Generic 32-bit Test Device**：UInt32 / Int32 / Float32（四種 Order、NaN / ±Infinity）固定資料（Reg 1～34，唯讀）與可寫 Raw 區（Reg 101～102） |

> **Unit 2 是 SIMULATOR TEST FIXTURE**，不是真實設備，也不是 Device Profile。
> Simulator 的行為（例如唯讀寫入回 Exception 02、FC16、重啟還原）**不代表**真實 Sensor 的行為。
> Register map 詳見 `modbus_tcp_server.py` 開頭與 `GENERIC32_FIXTURES`。

### 建立 venv（依 `requirements.txt`：`pymodbus==3.15.0`）

```powershell
cd tools\pi_modbus_server
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

### 背景啟動 / 狀態 / 停止（預設 `127.0.0.1:5020`）

```powershell
.\Start_ModbusTCP.ps1      # 背景啟動（已在執行就不重複啟動），等到 port 真的在 listen
.\Status_ModbusTCP.ps1     # Running / PID / Port / Listen
.\Stop_ModbusTCP.ps1       # 只停止 modbus_tcp_server.py
```

Log：`tools\pi_modbus_server\modbus_tcp_server.log`（每行標示 `Unit 1` / `Unit 2`）。
修改 Simulator 原始碼後，必須 Stop → Start，才會跑新版。

Tester 連線：Modbus TCP、IP `127.0.0.1`、Port `5020`、Unit ID `1`（A6V）或 `2`（Generic 32-bit）。

### Simulator 測試

```powershell
cd tools\pi_modbus_server
.\.venv\Scripts\python.exe -B -m unittest test_modbus_tcp_server -v
```

### `SimulatorTcpIntegrationTests` 的前提

App 測試中的 `SimulatorTcpIntegrationTests` 會用 `tools\pi_modbus_server\.venv` **自己啟動一個 Simulator**
（臨時 port，測完停止），不影響背景的 5020。若沒有 `.venv`，這些測試會失敗並顯示
`Simulator venv not found`。測試不會自行下載 Python、安裝 pymodbus 或修改全域 Python。

---

## 6. Modbus RTU 使用說明

UI 可設定：COM Port（Refresh COM 重新掃描）、Baud Rate（9600 / 19200 / 38400 / 57600 / 76800 / 115200）、
Data Bits（8）、Parity（None / Even / Odd）、Stop Bits（1 / 2）、Slave ID（1～247）、Timeout（1～60000 ms）。

**已驗證環境範例**（不是所有 Modbus RTU 設備的固定設定）：

| 項目 | 值 |
|---|---|
| USB-RS485 | UT-850N（Windows COM3） |
| 接線 | Sensor `+` / `-` ↔ UT-850N `T/R+` / `T/R-` |
| 參數 | 9600 / 8 / Even / 1，Slave ID 1，Timeout 1000 ms |

> Sensor 文件的 Baud rate 預設是 Register 765 = 0（Auto）；Tester 的 9600 只是初始嘗試值。
> 讀到 Register 765 / 766 **不會**自動改變 COM Port 設定。

---

## 7. A6V12258321 Profile

| Register | 內容 | Access |
|---|---|---|
| 1 / 2 | Temperature value / reliability | R |
| 3 / 4 | Relative humidity value / reliability | R |
| 5 / 6 | CO2 value / reliability | R |
| 223 / 224 / 225 | Temperature / Humidity / CO2 offset | RW |
| 401 | System unit（0 Celsius / 1 Fahrenheit） | RW |
| 764 / 765 / 766 | Modbus address / Baud rate / Transmission format | RW（**Tester：WriteOnHold**） |
| 1286 / 1287 | Software version（Major.Minor / Build） | R |

- Register 編號從 1 開始（Register N = PDU N − 1），只支援 Holding Register（FC03）
- 764～766 在 Tester 中**不會送出寫入**（WriteOnHold），不代表已實機驗證可寫

---

## 8. Known Limitations / Verification Status

### 已實機驗證（Siemens Sensor，RTU，UT-850N）

- RTU FC03 讀取：Reg 1～6、223～225、401、764～766、1286～1287，以及 Profile cumulative read（5 requests / 15 rows）
- Reg 401：0 / 1 時 Sensor 自己把 Reg 1 輸出為 Celsius / Fahrenheit ×100（Tester 不換算）
- Reg 223：Celsius context，0 → +0.1 °C → 0，FC06 Write / Verify / Restore
- Reg 224：0 → +0.1 % → 0，FC06 Write / Verify / Restore
- Software Version 解碼（實機 `1.00.7`）

### 尚未實機驗證

| 項目 | 狀態 |
|---|---|
| Reg 223 在 Fahrenheit 下的 Sensor 行為 | PENDING PHYSICAL VERIFICATION |
| Reg 225 Write | PENDING / DO NOT WRITE |
| Reg 764～766 Write | PENDING / WriteOnHold |
| Sensor FC16 支援 | NOT VERIFIED |
| Offset 是否保存（非揮發） | NOT VERIFIED |
| 無效值寫入時 Sensor 的 Exception | NOT VERIFIED |

> Simulator / 軟體測試 PASS **不等於** 實機 PASS。

### 其他限制

- 32-bit 只支援讀取；UInt32 / Int32 / Float32 的**工程值寫入**尚未提供（可用 FC06 / FC16 Raw 寫入）
- 不支援 Coil / Discrete Input（FC01 / FC02 / FC05 / FC15）
- Data Bits 只提供 8
- Communication Log 畫面保留最近 5000 筆
