using VitaTrace.Core.Models;

namespace VitaTrace.Core.Abstractions;

/// <summary>
/// 设备枚举器。Phase 3 实现 USB/HID/BLE 枚举；Phase 2 只定义契约，避免 UI 直接依赖 Win32。
/// </summary>
public interface IDeviceEnumerator
{
    Task<IReadOnlyList<PeripheralDevice>> EnumerateAsync(CancellationToken cancellationToken = default);
}
