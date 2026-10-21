# 第三阶段：设备枚举与 HID 识别

> 状态：第一阶段增量已完成并通过本机验证。
> 本阶段只解决“系统里有哪些 USB / HID / 蓝牙 HID 设备、哪个可能是 2.4G 接收器”。
> 电量读取仍然留到第四阶段；真实设备不会出现任何假百分比。

---

## 1. 目标与边界

本阶段要回答三个问题：

1. 系统里有哪些 HID 设备接口？
2. 哪些接口属于同一个物理设备？哪个物理设备可能是 2.4G 接收器？
3. 插入、拔出、重新连接后，界面能不能自动更新？

本阶段**不做**：

- 不读取电量（第四阶段）。
- 不猜测接收器里配对的是哪一款键盘/鼠标；无法确认时只显示接收器。
- 不枚举非 HID 的 BLE 设备（例如 BLE 耳机）；蓝牙 HID 键鼠可以枚举。
- 不安装驱动、不注册服务、不要求管理员权限。

---

## 2. 代码结构

```text
src/VitaTrace.Infrastructure/Devices/
├─ HidNativeMethods.cs              SetupAPI / HID / CfgMgr32 的 P/Invoke
└─ WindowsHidDeviceEnumerator.cs    HID 枚举、物理设备合并、接收器启发式
```

接入点：

- `src/VitaTrace.App/App.xaml.cs`：真实模式使用 `WindowsHidDeviceEnumerator`；演示模式仍用 `DemoDeviceEnumerator`。
- `src/VitaTrace.App/MainWindow.xaml.cs`：监听 `WM_DEVICECHANGE`，800ms 防抖后触发 `MainViewModel.RefreshAsync()`。
- `src/VitaTrace.App/MainWindow.xaml`、`Views/SettingsWindow.xaml`：隐藏竖向滚动条，保留鼠标滚轮。

依赖方向没有变化：

```text
App  ──►  Core
 │
 └────►  Infrastructure  ──►  Core
```

Win32 细节全部留在 Infrastructure；Core 仍然只有模型和接口。

---

## 3. 枚举流程

### 3.1 枚举 HID 接口

```text
HidD_GetHidGuid
  ↓
SetupDiGetClassDevs(GUID_DEVINTERFACE_HID, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
  ↓
SetupDiEnumDeviceInterfaces
  ↓
SetupDiGetDeviceInterfaceDetail   → 设备路径 \?\hid#...
  ↓
CreateFile(路径, 共享读写)         → 读取 HID 属性
```

每个接口读取：

| 数据 | API |
| --- | --- |
| VID / PID / 版本 | `HidD_GetAttributes` |
| 产品名 | `HidD_GetProductString` |
| 厂商名 | `HidD_GetManufacturerString` |
| 序列号 | `HidD_GetSerialNumberString` |
| Usage Page / Usage | `HidD_GetPreparsedData` + `HidP_GetCaps` |
| FriendlyName / DeviceDesc / MFG | `SetupDiGetDeviceRegistryProperty` |

如果厂商软件独占设备导致 `CreateFile` 失败，会退到“0 访问”只读属性；仍然失败时从设备实例 ID 解析 VID/PID，不把设备直接丢掉。

### 3.2 合并同一个物理设备

一个 USB 接收器/复合设备会暴露多个 HID 集合（键盘、鼠标、厂商自定义）。如果每个集合都生成一张卡片，界面会重复。

解决方式：

1. 从 HID 接口的设备实例向上调用 `CM_Get_Parent`；
2. 跳过 `USB\VID_...&MI_00` 这样的接口层；
3. 找到 `USB\VID_...` 或 `BTHENUM\...` / `BTHLE\...` 物理层；
4. 用物理设备 ID 作为分组键；
5. 同一组只生成一个 `PeripheralDevice`。

### 3.3 只保留外部设备

HID 接口枚举会包含笔记本内部设备：

- `HID Button over Interrupt Driver`（ACPI 电源/功能键）
- `HIDI2C Device`（触摸板）
- `Virtual Multitouch Device`（虚拟多点触控）

这些不是用户要监控的外设。当前策略是：**物理父设备不是 `USB\` / `BTHENUM\` / `BTHLE\` 的 HID 接口直接跳过**。这样不会把系统内部设备伪装成外设。

---

## 4. 设备分类与接收器判断

### 4.1 类型

按 HID Usage 分类，优先级从高到低：

| 条件 | 类型 |
| --- | --- |
| Usage Page `0x01` / Usage `0x06` | 键盘 |
| Usage Page `0x01` / Usage `0x02` | 鼠标 |
| Usage Page `0x01` / Usage `0x04` 或 `0x05` | 手柄 |
| 名称包含 headset / headphone / 耳机 | 耳机 |
| 其他 | 其他外设 |

### 4.2 连接方式

- 物理 ID 以 `BTHENUM\` / `BTHLE\` 开头：蓝牙；
- 物理 ID 以 `USB\` 开头：有线或 2.4G 接收器通道；
- 识别为接收器时：`UsbReceiver`。

### 4.3 接收器启发式

满足任一条件才标记为接收器：

1. 产品名/友好名包含 `receiver`、`接收器`、`unifying`、`lightspeed`、`bolt`、`2.4g`；
2. 罗技 VID `0x046D` 且 PID 在已知接收器列表（Unifying / Bolt / Lightspeed 常见 PID）。

**没有**使用“多个 HID 集合 + 厂商 Usage Page”作为判据，因为很多游戏键盘也有厂商自定义集合，会误判。名称和已知 VID/PID 之外的情况宁可保留为未知设备。

接收器名称也做了防错：优先选名称里明确带 Receiver/接收器 的 HID 集合；如果只有子设备的产品名，不拿子设备名当接收器名，而是回退到“厂商 + USB 接收器”。

### 4.4 电池状态

本阶段不读电量，所以：

| 设备 | BatteryStatus | UI |
| --- | --- | --- |
| 接收器 | `Wired` | 有线供电；接收器本身没有电池 |
| USB 有线 HID | `Wired` | 有线供电 |
| 蓝牙 HID | `Unknown` | 未知，等待第四阶段读取 |
| 其他 | `Unknown` | 未知 |

**没有任何 `BatteryReading` 被创建，也没有任何百分比。** 这是刻意的：第三阶段只证明“识别”，不冒充“已能读电量”。

---

## 5. 热插拔

主窗口在 `OnSourceInitialized` 里挂 `HwndSource` 钩子：

```text
WM_DEVICECHANGE (0x0219)
  + DBT_DEVNODES_CHANGED (0x0007)
  → 800ms 防抖
  → MainViewModel.RefreshAsync()
```

- `DBT_DEVNODES_CHANGED` 是系统广播，不需要 `RegisterDeviceNotification`，也不需要管理员权限。
- 一次 USB 插入会带来多个节点变化，防抖避免连续枚举。
- 如果当前正在刷新，定时器会再等一轮，避免重入。

---

## 6. 界面调整：隐藏滚动条

用户要求去掉界面右侧的竖向滚动条。改动：

- `MainWindow.xaml` 设备列表 `ListBox`：`ScrollViewer.VerticalScrollBarVisibility="Hidden"`；
- `MainWindow.xaml` 右侧详情 `ScrollViewer`：`VerticalScrollBarVisibility="Hidden"`；
- `SettingsWindow.xaml` 设置内容 `ScrollViewer`：`VerticalScrollBarVisibility="Hidden"`。

隐藏的是**滚动条**，不是鼠标滚轮：内容超出时仍可用滚轮/触摸滚动，只是不再常驻一条竖线。

---

## 7. 本机验证记录

真实模式截图（YICHIP 2.4G 接收器，电量圆环显示“—”，无假数据）：

![真实模式](images/main-real.png)

测试环境：Windows 11 25H2，150% DPI，机器上有 YICHIP 2.4G 接收器。

真实模式日志（`VITATRACE_DATA_DIR=E:\Vita Trace\.tmp\appdata-real`）：

```text
HID 枚举完成：15 个接口，合并为 4 个设备
  HID Button over Interrupt Driver | Other | Wired | ...
  HIDI2C Device | Other | Wired | VID_093A PID_0255 | ...
  Virtual Multitouch Device | Other | Wired | VID_00FF PID_BACC | ...
  YICHIP USB 接收器 | Receiver | UsbReceiver | VID_3151 PID_3020 | ...
```

加入外部设备过滤后：

```text
HID 枚举完成：5 个接口，合并为 1 个设备
  YICHIP USB 接收器 | Receiver | UsbReceiver | VID_3151 PID_3020 | ...
```

主界面显示：

- 设备数：1；
- 名称：YICHIP USB 接收器；
- 类型：接收器；
- 连接：2.4G 接收器；
- 状态：有线供电；
- 电量圆环：`—`；
- 提示：USB 总线供电；接收器本身没有电池，已配对的键鼠型号需要厂商协议才能识别。

热插拔验证：

- 用 `PostMessage(WM_DEVICECHANGE, DBT_DEVNODES_CHANGED)` 模拟设备节点变化；
- 日志出现第二次 `HID 枚举完成`，证明防抖刷新链路生效；
- **未做物理插拔**（当前测试环境没有可插拔的第二套接收器），这一点如实记录。

滚动条验证：

- 真实模式主窗口截图无右侧竖条；
- 设置窗口截图无右侧竖条；
- 鼠标滚轮滚动未受影响。

---

## 8. 已知限制

1. **非 HID 的 BLE 设备尚未枚举**：BLE 耳机、部分 BLE 鼠标可能不暴露 HID 接口，需要第四阶段用 PnP 属性 / GATT 枚举。
2. **接收器内配对设备未展开**：非罗技接收器通常不提供“接收器 → 外设”的公开映射；当前只显示接收器，不编造“已连接键盘/鼠标”。
3. **罗技 HID++ 未接入**：第四阶段才会通过 feature `0x1000` / `0x1001` 读取配对设备电量。
4. **蓝牙 HID 未真机验证**：代码按 `BTHENUM` / `BTHLE` 识别，但当前机器没有蓝牙键鼠。
5. **内部 HID 过滤是白名单式**：只保留 USB/蓝牙物理父设备；极少数通过非标准总线暴露的 HID 设备会被跳过。
6. **热插拔只验证了消息链**：物理插拔、重新连接仍需要在第四阶段用真实设备复测。

---

## 9. 验收清单

- [x] `build.cmd` 成功，0 warning / 0 error。
- [x] 真实模式能枚举到 YICHIP USB 接收器（VID 3151 / PID 3020）。
- [x] 接收器显示为“接收器 / 2.4G 接收器 / 有线供电”，不显示假电量。
- [x] 内部 HID（HIDI2C、虚拟多点触控、ACPI 按钮）被过滤。
- [x] `WM_DEVICECHANGE` 能触发第二次枚举。
- [x] 主界面、设置窗口右侧竖条已隐藏，滚轮仍可用。
- [x] 演示模式仍未受影响（`--demo`）。
- [ ] 物理插拔/重连真机复测（第四阶段）。
- [ ] 蓝牙 HID 真机复测（需要设备）。

---

## 10. 下一阶段入口

第四阶段：电量读取与适配。

优先顺序：

1. **罗技 HID++ 2.0**：feature `0x1000` / `0x1001`，报告 ID `0x10/0x11`，device index `0xFF` 接收器 / `1–6` 配对设备；先做能力探测，再读取。
2. **BLE PnP 电量**：`Get-PnpDeviceProperty` 对应的 `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`；只对 BLE 设备使用。
3. **标准 HID Battery Strength**：Usage Page `0x06` / Usage `0x20`，解析报告描述符；支持设备很少，读到才显示。
4. **失败状态**：读取失败、超时、设备忙、厂商软件占用分别映射到 `ReadFailed` / `Unsupported`，不生成数字。

验收时必须用真实设备对比厂商软件/系统设置页的电量，并记录误差与刷新延迟。
