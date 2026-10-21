# Vita Trace

> Windows 无线外设电量监控工具。把接收器、蓝牙设备和有线外设放到一个窗口里，尽量读真实电量；读不到就明确说读不到。

![Vita Trace 主界面](docs/images/main-light.png)

当前版本：**0.7.0（第七阶段：测试与打包）**

---

## 现在能做什么

- 统一的设备列表：展示设备名称、类型、连接方式、电量、状态和更新时间；每台设备左侧有从 1 开始的序号，和托盘圆圈里的号码一一对应。
- 设备重命名：标题右侧铅笔按钮，Enter 保存、Esc 取消；留空恢复系统名称。别名会写入本地设置，重启后保留。
- 电量圆环：0–100% 圆弧 + 圈内百分比；未知/不支持/断开时显示 `—`，不伪造数值。
- 电量历史曲线：固定 0–100 纵轴，缺失值断线，悬停显示采样时间和电量；真实读数会写入本地 `history.json`，重启后继续显示。
- **历史持久化（第五阶段）**：只记录成功读数；电量变化才采点，不变时最多 10 分钟补一个点；默认保留 90 天，启动时自动加载最近 60 个点。
- 明暗主题、简体中文 / English 切换。
- 设置窗口：刷新间隔、低电量阈值、历史保留、开机启动、关闭进托盘、低电量提醒。
- 多设备托盘（第五阶段增量）：**每台读得到电量的设备一个圆环图标**，圆环的闭合弧度就是电量，圈内是列表序号；≥20% 绿色，<20% 红色。鼠标悬停显示名称/电量，单击打开并选中该设备；右键菜单只保留“刷新”和“退出”（打开窗口直接单击图标即可）。
- 三处分工：任务栏大图标和 `^` 里的图标都是 **V 应用图标**（大图标与它的缩小版）；`^` 旁边才是每台设备的电量圆环。Windows 一个托盘图标只能出现在一个位置，所以应用图标保持 `IsPromoted=0` 留在 `^`，设备圆环设 `IsPromoted=1` 固定在 `^` 旁。
- 读不到电量的设备（不支持、读取失败、接收器、有线、断开）不生成托盘图标，避免用空图标冒充设备。
- 托盘：关闭窗口默认进入托盘；再次双击 Vita Trace 会唤回隐藏窗口。
- Windows 11 默认把新托盘图标收进 `^` 溢出区。应用会通过当前用户的 `NotifyIconSettings\IsPromoted` 请求把这些圆圈固定到 `^` 旁边；如果第一次启动后仍看不到，重启一次 Explorer（任务管理器 → 重启 Windows 资源管理器）或到“设置 → 个性化 → 任务栏 → 其他系统托盘图标”把 Vita Trace 打开。
- 托盘小图标需要应用与 Explorer 处于同一完整性级别：请从资源管理器双击 `Vita Trace.lnk` / `启动 Vita Trace.vbs` 启动，不要从管理员终端、IDE 调试器或受限沙箱直接启动，否则 `Shell_NotifyIcon` 会被 Windows 拒绝（Win32 error 5，Access Denied），表现为任务栏有窗口按钮、`^` 里没有图标。
- 应用日志里的 `托盘同步：N 个设备，visible=True` 表示 Shell 接受了 N 个图标；`visible=False, error=err=5` 表示当前启动方式的完整性级别低于任务栏，需要关闭应用后从资源管理器重新双击启动。
- Explorer 重启后会广播 `TaskbarCreated`，应用会自动重新注册所有圆圈，不需要手动重启 Vita Trace。
- 即使托盘图标被系统吞掉，再次双击 Vita Trace 也会把已隐藏的旧窗口唤回，不会被“已在运行”挡住。
- 设备路径只写入日志，不在界面显示，避免把 `\\?\hid#...` 这类内部标识暴露给用户。
- 低电量：对应设备的托盘圆圈变红 + 气泡提醒；窗口可见时闪任务栏；每台设备只提醒一次，恢复后再次跌破会重新提醒。
- 演示模式：`--demo` 显示 8 个示例设备，界面有明确的“演示数据”横幅。
- **真实 HID 枚举（第三阶段）**：SetupAPI + HID API 枚举 USB / 蓝牙 HID 设备，按物理设备合并重复接口。
- **接收器识别**：已知 VID/PID + 名称关键词判断 2.4G 接收器；接收器单独显示，不把 HID 集合伪装成已配对键鼠。
- **热插拔**：监听 `WM_DEVICECHANGE`，插入/拔出后自动刷新。
- **无滚动条界面**：主界面和设置页隐藏竖向滚动条，保留鼠标滚轮。
- **雷柏 2.4G 电量（已实测）**：读取接收器主动推送的 `FF00:0002` / Report ID `0x07` 状态帧，`data[8]` 为百分比；本机 VT7 Max 读到 69%，与官方显示一致。
- **迈从 G98 Pro 键盘电量（已实测）**：对 `FF02:0002` 发送 20 字节 `0x13 / 0x4A` 查询，解析 `0x13` 输入帧与校验和；本机读到 100%。
- **其他厂商协议（验证中）**：CompX 64 字节 Feature 查询、MCHOSE `0x11/0x13` 鼠标报告解码；只对已知 VID 尝试，失败明确标记。
- **BLE PnP 电量**：对蓝牙设备读取 Windows 的 Battery 属性，作为通用兜底；无属性时标“不支持”。
- **只读 HID 探针**：`--probe` 记录每个 HID 接口的 Usage、Input/Output/Feature 长度和协议收发字节，用于确认真实硬件能力。
- **稳定性（第六阶段）**：单个读取器最多等 6 秒；本轮失败但 10 分钟内有过有效读数时，显示上次值并标记“可能过期”，不把旧值伪装成实时值；刷新超过 1.5 秒写入日志。

> 第四阶段已修复 `HidP_GetCaps` 状态码误判（`HIDP_STATUS_SUCCESS = 0x00110000`）。
> 雷柏 VT7 Max（`24AE:1416`）已通过被动状态帧读到实际电量；
> 迈从 G98 Pro（SINOWEALTH `258A:01BB`）已通过键盘 `0x13/0x4A` 读到实际电量；
> ERAZER 的 YICHIP `3151:3020` 没有可验证的电量通路，仍显示 `—`，不会用缓存或默认值冒充。

## 技术栈

- WPF + .NET 10（LTS）
- MVVM，分层：`VitaTrace.App` → `VitaTrace.Core` / `VitaTrace.Infrastructure`
- 托盘：原生 `Shell_NotifyIcon`（每台可读设备一个圆圈，不依赖 WinForms）
- 设置：`System.Text.Json` + 本地文件
- 图表、圆环：自绘 WPF 控件，无第三方图表库
- 当前**没有第三方 NuGet 依赖**

## 快速开始

### 1. 环境

- Windows 10 1809+ 或 Windows 11
- .NET SDK 10.0.4xx

本仓库的开发机把 SDK 放在项目内 `.dotnet\`（不写系统目录、不改 PATH）。如果你使用系统安装的 SDK，把下面命令里的 `.dotnet\dotnet.exe` 换成 `dotnet` 即可。

### 2. 构建

```powershell
.\build.cmd
```

`build.cmd` 只是用 `-ExecutionPolicy Bypass` 调用 `build.ps1`，不会修改系统执行策略。如果你的终端允许运行脚本，也可以直接执行 `.\build.ps1`。

手动构建：

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
$env:NUGET_PACKAGES   = "$PWD\.nuget\packages"
& ".\.dotnet\dotnet.exe" build ".\VitaTrace.slnx" `
    -c Debug -m:1 -p:UseSharedCompilation=false
```

### 3. 运行

演示模式（推荐先看界面）：

```powershell
$env:DOTNET_ROOT = "$PWD\.dotnet"
$env:VITATRACE_DATA_DIR = "$PWD\.tmp\appdata-demo"
& ".\.dotnet\dotnet.exe" ".\src\VitaTrace.App\bin\Debug\net10.0-windows\VitaTrace.dll" --demo
```

真实模式（枚举本机 USB / 蓝牙 HID 设备）：

```powershell
$env:DOTNET_ROOT = "$PWD\.dotnet"
& ".\.dotnet\dotnet.exe" ".\src\VitaTrace.App\bin\Debug\net10.0-windows\VitaTrace.dll"
```

真实模式只显示系统里实际存在的 USB / 蓝牙 HID 设备。接收器本身显示“有线供电”；外设只有在读取器返回有效百分比时才显示数字，否则保持“未知 / 不支持 / 读取失败”。雷柏接收器在鼠标休眠时可能不推送状态帧，移动鼠标后刷新即可。

`VITATRACE_DATA_DIR` 只用于把测试数据写进工作区；正式运行省略，数据在 `%LOCALAPPDATA%\VitaTrace`。

### 3.1 只读 HID 探针

插上要排查的接收器后运行：

```powershell
$env:DOTNET_ROOT = "$PWD\.dotnet"
& ".\.dotnet\dotnet.exe" ".\src\VitaTrace.App\bin\Debug\net10.0-windows\VitaTrace.dll" --probe
```

探针不建窗口，只把每个 HID 接口的 Usage、Input/Output/Feature 长度、
雷柏状态帧、CompX / MCHOSE 查询的收发字节和 Win32 错误写入 `logs\app.log`。
它只做查询，不修改 DPI、回报率、灯光等设备设置。

### 4. 发布与打包

```powershell
.\build.cmd -Configuration Release -Publish   # 只发布，不打 zip
powershell -NoProfile -ExecutionPolicy Bypass -File .\package.ps1
```

`package.ps1` 会构建 Release 自包含单文件 exe，生成：

```text
dist\VitaTrace-0.7.0-win-x64\
├─ VitaTrace.exe        # 自包含单文件，不需要安装 .NET
├─ README.md
├─ install.ps1          # 按用户安装（不写 Program Files，不需要管理员）
└─ uninstall.ps1        # 卸载；-RemoveData 同时删除设置/日志/历史
dist\VitaTrace-0.7.0-win-x64.zip
dist\VitaTrace-0.7.0-win-x64.zip.sha256
```

当前发布包约 61 MB（压缩后），SHA256 见 `.sha256` 文件。未做代码签名，Windows SmartScreen 可能提示“未知发布者”；这是当前阶段的已知限制，正式分发前应签名。

拿到 CA 证书后，用 `sign-package.ps1` 签名并重新打包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\sign-package.ps1 `
  -Version 0.7.0 -Thumbprint "ABCDEF..."
```

当前另有一个**自签名测试包** `dist\VitaTrace-0.7.0-test-signed-win-x64.zip`，只用于验证签名/时间戳流程，不会让 SmartScreen 显示可信发布者，不要对外分发。签名方案与证书选型见 [代码签名](docs/阶段8-代码签名.md)。

#### 直接运行（绿色版）

解压后双击 `VitaTrace.exe` 即可。首次运行会在 `%LOCALAPPDATA%\VitaTrace` 创建设置、日志与历史。

#### 按用户安装

解压后右键 `install.ps1` → “使用 PowerShell 运行”，或：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

安装到 `%LOCALAPPDATA%\Programs\VitaTrace`，并在开始菜单创建快捷方式。卸载：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\VitaTrace\uninstall.ps1"
# 可选：同时删除设置/日志/历史
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\VitaTrace\uninstall.ps1" -RemoveData
```

## 项目结构

```text
src/
├─ VitaTrace.Core/            纯模型与接口，不依赖 WPF
├─ VitaTrace.Infrastructure/  设置、日志、路径、HID 枚举
│  └─ Devices/                SetupAPI/HID P/Invoke、真实枚举器、雷柏/CompX/MCHOSE/PnP 读取器
└─ VitaTrace.App/             WPF 界面、控件、ViewModel、托盘
docs/                         阶段报告与设计文档
VitaTrace.slnx                解决方案
build.cmd                     构建入口（绕过执行策略限制）
build.ps1                     构建/发布脚本
package.ps1                   生成便携 zip 与 SHA256
packaging/                    install.ps1 / uninstall.ps1（按用户安装）
```

更完整的设计说明：

- [第二阶段：技术架构与 UI 原型](docs/阶段2-技术架构与UI原型.md)
- [第三阶段：设备枚举与 HID 识别](docs/阶段3-设备枚举与HID识别.md)
- [第四阶段：电量读取与协议验证](docs/阶段4-电量读取与协议验证.md)
- [第五阶段：历史记录与低电量提醒](docs/阶段5-历史与提醒.md)
- [第六阶段：稳定性、性能与兼容性](docs/阶段6-稳定性与性能.md)

## 数据与隐私

- 设置：`%LOCALAPPDATA%\VitaTrace\settings.json`
- 日志：`%LOCALAPPDATA%\VitaTrace\logs\app.log`
- 历史：`%LOCALAPPDATA%\VitaTrace\history.json`（90 天，只有成功读数）
- 不联网、不上传设备信息；所有数据只在本机。
- 不需要管理员权限；开机启动只写当前用户的 `HKCU\...\Run`。

## 设计原则

1. **不伪造数据**：读不到就显示“不支持读取 / 读取失败 / 设备已断开”，不生成百分比。
2. **接收器和外设分开**：接收器没有电池，显示“有线供电”；只有确认能读到外设电量时才显示百分比。
3. **状态优先于数值**：`Ok / Stale / ReadFailed / Unsupported / Disconnected / ReceiverOffline / Wired` 分别呈现。
4. **权限最小化**：不装驱动、不改系统设置、不需要管理员。
5. **可替换**：设备枚举与电量读取都在接口后面，第三阶段替换实现不影响界面。

## 已知限制

- 非罗技 2.4G 接收器通常不暴露“接收器 → 外设”的映射；当前只显示接收器，不编造已配对键鼠。
- 非 HID 的 BLE 设备（BLE 耳机等）尚未枚举；第四阶段用 PnP / GATT 补齐。
- 蓝牙经典设备多数没有标准电量通道；BLE 的 PnP 缓存可能滞后或卡在 100%。
- 罗技 HID++ 需要逐型号能力探测，不能假设所有罗技设备都支持。
- 当前机器的 YICHIP `3151:3020` 接收器（用户确认是 ERAZER 办公鼠标的接收器）没有 64 字节 Feature，CompX 不可用；MCHOSE Output 查询被拒绝，`HidD_GetInputReport` / `HidD_GetFeature` 也没有返回报告，因此该鼠标只能标“不支持读取”。
- 迈从 G98 Pro 的 SINOWEALTH `258A:01BB` 接收器使用键盘专用 `0x13/0x4A` 命令，与 MCHOSE 鼠标的 `0x11/0x13` 协议不同；不要混用。
- 雷柏 VT7 Max 的状态帧已读到 69%，用户确认与官方显示一致；雷柏配置寄存器没有可靠电量寄存器，本项目不使用其缓存/默认 100% 逻辑。
- 雷柏鼠标休眠时可能不推送状态帧，此时显示“读取失败”；移动鼠标后刷新即可。
- BLE PnP 电量读取器已实现并编译通过，但当前没有 BLE 设备，尚未真机验证。

## 路线图

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| 1 | 需求分析与技术可行性 | 已完成 |
| 2 | 技术栈、架构、UI 原型 | 已完成 |
| 3 | 真实 USB / HID / 蓝牙 HID 枚举 | 已完成第一增量；BLE 非 HID 待补 |
| 4 | 真实电量读取与适配 | 已完成第一增量：雷柏 69%、G98 Pro 100% 已实测；ERAZER 无可用通路 |
| 5 | 主界面完善、提醒、历史 | 已完成：history.json、90 天清理、多设备低电量提醒 |
| 6 | 异常、日志、性能、兼容性 | 已完成第一增量：6 秒读取超时、Stale 回退、慢读取日志、设置校验 |
| 7 | 测试、打包、发布 | 已完成：单文件发布、zip、按用户安装/卸载脚本；待签名与干净机验收 |

## 文档

- [第一阶段：需求分析与技术可行性](docs/阶段1-需求分析与技术可行性报告.md)
- [第二阶段：技术架构与 UI 原型](docs/阶段2-技术架构与UI原型.md)
- [第三阶段：设备枚举与 HID 识别](docs/阶段3-设备枚举与HID识别.md)
- [第四阶段：电量读取与协议验证](docs/阶段4-电量读取与协议验证.md)
- [第五阶段：历史与提醒](docs/阶段5-历史与提醒.md)
- [第六阶段：稳定性与性能](docs/阶段6-稳定性与性能.md)
- [第七阶段：测试与打包](docs/阶段7-测试与打包.md)
- [代码签名](docs/阶段8-代码签名.md)
- [虚拟机验收指南](docs/阶段9-虚拟机验收.md)
