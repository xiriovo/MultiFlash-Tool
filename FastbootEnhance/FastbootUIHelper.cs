using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OPFlashTool.FastbootEnhance
{
    /// <summary>
    /// Fastboot UI 辅助类
    /// 借鉴 libxzr/FastbootEnhance 的 UI 设计
    /// </summary>
    public class FastbootUIHelper
    {
        private readonly FastbootService _fastbootService;
        private readonly Action<string> _logCallback;
        private readonly Action<string, Color> _colorLogCallback;
        private CancellationTokenSource _devicePollingCts;
        private bool _isPolling;

        public event Action<List<FastbootDeviceInfo>> OnDevicesChanged;
        public event Action<FastbootDeviceData> OnDeviceDataLoaded;
        public event Action<bool> OnConnectionStateChanged;

        public FastbootUIHelper(FastbootService fastbootService, Action<string> logCallback, Action<string, Color> colorLogCallback = null)
        {
            _fastbootService = fastbootService;
            _logCallback = logCallback;
            _colorLogCallback = colorLogCallback;
        }

        private void Log(string message) => _logCallback?.Invoke(message);
        private void Log(string message, Color color) => _colorLogCallback?.Invoke(message, color);

        /// <summary>
        /// 开始设备轮询
        /// </summary>
        public void StartDevicePolling(int intervalMs = 1000)
        {
            if (_isPolling) return;

            _isPolling = true;
            _devicePollingCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                var lastDevices = new List<FastbootDeviceInfo>();

                while (!_devicePollingCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var devices = await _fastbootService.GetDevicesAsync(_devicePollingCts.Token);
                        var currentDevices = devices.Select(d => new FastbootDeviceInfo
                        {
                            Serial = d.serial,
                            IsFastbootD = d.isFastbootD,
                            Mode = d.isFastbootD ? "FastbootD" : "Fastboot"
                        }).ToList();

                        // 检测设备变化
                        if (!AreDeviceListsEqual(lastDevices, currentDevices))
                        {
                            lastDevices = currentDevices;
                            OnDevicesChanged?.Invoke(currentDevices);
                        }

                        await Task.Delay(intervalMs, _devicePollingCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"设备轮询异常: {ex.Message}");
                        await Task.Delay(intervalMs, _devicePollingCts.Token);
                    }
                }
            });
        }

        /// <summary>
        /// 停止设备轮询
        /// </summary>
        public void StopDevicePolling()
        {
            _isPolling = false;
            _devicePollingCts?.Cancel();
        }

        private bool AreDeviceListsEqual(List<FastbootDeviceInfo> a, List<FastbootDeviceInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Serial != b[i].Serial || a[i].IsFastbootD != b[i].IsFastbootD)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 连接到设备
        /// </summary>
        public async Task<bool> ConnectToDeviceAsync(string serial)
        {
            var result = await _fastbootService.ConnectAsync(serial);
            OnConnectionStateChanged?.Invoke(result);

            if (result)
            {
                OnDeviceDataLoaded?.Invoke(_fastbootService.DeviceData);
            }

            return result;
        }

        /// <summary>
        /// 显示设备信息列表
        /// </summary>
        public List<KeyValuePair<string, string>> GetDeviceInfoList(FastbootDeviceData data)
        {
            var list = new List<KeyValuePair<string, string>>();

            if (data == null) return list;

            list.Add(new KeyValuePair<string, string>("设备型号", data.Product ?? "Unknown"));
            list.Add(new KeyValuePair<string, string>("序列号", data.SerialNo ?? "Unknown"));
            
            if (!string.IsNullOrEmpty(data.Variant))
                list.Add(new KeyValuePair<string, string>("变体", data.Variant));
            
            list.Add(new KeyValuePair<string, string>("安全启动", data.Secure ? "已启用" : "已禁用"));
            list.Add(new KeyValuePair<string, string>("Bootloader", data.Unlocked ? "已解锁" : "已锁定"));
            
            list.Add(new KeyValuePair<string, string>("A/B 分区", data.HasSlot ? "是" : "否"));
            if (data.HasSlot)
                list.Add(new KeyValuePair<string, string>("当前槽位", data.CurrentSlot?.ToUpper() ?? "Unknown"));
            
            list.Add(new KeyValuePair<string, string>("FastbootD 模式", data.IsFastbootD ? "是" : "否"));
            
            if (data.MaxDownloadSize > 0)
                list.Add(new KeyValuePair<string, string>("最大下载大小", FastbootDeviceData.FormatSize(data.MaxDownloadSize)));

            // VAB 状态
            var vabStatus = VabManager.ParseStatus(data.SnapshotUpdateStatus);
            if (vabStatus != VabManager.VabStatus.None && vabStatus != VabManager.VabStatus.Unknown)
            {
                list.Add(new KeyValuePair<string, string>("VAB 更新状态", VabManager.GetStatusDescription(vabStatus)));
            }

            if (data.HasCowPartitions)
            {
                list.Add(new KeyValuePair<string, string>("COW 分区", $"{data.CowPartitions.Count} 个"));
            }

            return list;
        }

        /// <summary>
        /// 获取分区列表 (用于 ListView 显示)
        /// </summary>
        public List<FastbootPartitionInfo> GetPartitionList(FastbootDeviceData data)
        {
            var list = new List<FastbootPartitionInfo>();

            if (data == null) return list;

            foreach (var kvp in data.PartitionSizes.OrderBy(x => x.Key))
            {
                var isLogical = data.PartitionIsLogical.TryGetValue(kvp.Key, out var logical) && logical;
                var partitionType = data.PartitionTypes.TryGetValue(kvp.Key, out var type) ? type : "";

                list.Add(new FastbootPartitionInfo
                {
                    Name = kvp.Key,
                    Size = kvp.Value,
                    SizeFormatted = FastbootDeviceData.FormatSize(kvp.Value),
                    IsLogical = isLogical,
                    Type = partitionType,
                    IsCow = kvp.Key.EndsWith("-cow") || kvp.Key.EndsWith("_cow")
                });
            }

            return list;
        }

        /// <summary>
        /// 检查 VAB 状态并显示警告
        /// </summary>
        public async Task<bool> CheckVabAndConfirmAsync(FastbootDeviceData data, Func<string, string, Task<bool>> confirmCallback)
        {
            var (shouldWarn, message, level) = VabManager.CheckVabStatus(data);

            if (!shouldWarn) return true;

            var title = level == VabWarningLevel.Critical ? "⚠️ 危险警告" : "⚠️ 警告";
            return await confirmCallback(title, message + "\n\n是否继续?");
        }

        /// <summary>
        /// 检查 vbmeta 分区并询问用户
        /// </summary>
        public async Task<(bool proceed, bool disableVerity)> CheckVbmetaAndAskAsync(
            string partitionName, 
            Func<string, string, Task<bool>> confirmCallback)
        {
            if (!VbmetaHandler.IsVbmetaPartition(partitionName))
            {
                return (true, false);
            }

            var result = await confirmCallback(
                "vbmeta 分区检测",
                VbmetaHandler.GetWarningMessage() + "\n\n点击 '是' 禁用验证，点击 '否' 正常刷入");

            return (true, result);
        }

        /// <summary>
        /// 显示逻辑分区创建对话框
        /// </summary>
        public async Task<(bool success, string name, long size)> ShowCreateLogicalPartitionDialogAsync(
            Func<string, long, Task<(bool ok, string name, long size)>> dialogCallback)
        {
            return await dialogCallback("", 0);
        }

        /// <summary>
        /// 显示逻辑分区调整大小对话框
        /// </summary>
        public async Task<(bool success, long newSize)> ShowResizeLogicalPartitionDialogAsync(
            string partitionName, long currentSize,
            Func<string, long, Task<(bool ok, long size)>> dialogCallback)
        {
            return await dialogCallback(partitionName, currentSize);
        }

        /// <summary>
        /// 获取分区颜色 (用于 UI 高亮)
        /// </summary>
        public static Color GetPartitionColor(FastbootPartitionInfo partition)
        {
            var name = partition.Name.ToLowerInvariant();

            // COW 分区 - 橙色警告
            if (partition.IsCow)
                return Color.FromArgb(255, 152, 0);

            // 逻辑分区 - 蓝色
            if (partition.IsLogical)
                return Color.FromArgb(33, 150, 243);

            // 关键分区 - 红色
            if (name.Contains("bootloader") || name.Contains("xbl") || name.Contains("abl") ||
                name.StartsWith("sbl") || name.Contains("tz") || name.Contains("hyp"))
                return Color.FromArgb(244, 67, 54);

            // Boot 相关 - 橙色
            if (name.StartsWith("boot") || name.StartsWith("recovery") || 
                name.StartsWith("dtbo") || name.StartsWith("vbmeta"))
                return Color.FromArgb(255, 152, 0);

            // 系统分区 - 绿色
            if (name.StartsWith("system") || name.StartsWith("vendor") || 
                name.StartsWith("product") || name.StartsWith("odm"))
                return Color.FromArgb(76, 175, 80);

            // 用户数据 - 紫色
            if (name.StartsWith("userdata") || name.StartsWith("metadata"))
                return Color.FromArgb(156, 39, 176);

            // GPT - 灰色
            if (name.Contains("gpt") || name.Contains("partition"))
                return Color.FromArgb(158, 158, 158);

            // 默认 - 黑色
            return Color.Black;
        }

        /// <summary>
        /// 获取分区风险等级描述
        /// </summary>
        public static string GetPartitionRiskDescription(FastbootPartitionInfo partition)
        {
            var name = partition.Name.ToLowerInvariant();

            if (partition.IsCow)
                return "COW 分区 (VAB 更新相关)";

            if (name.Contains("bootloader") || name.Contains("xbl") || name.Contains("abl"))
                return "⚠️ 危险: 引导加载程序，刷错可能变砖";

            if (name.StartsWith("boot") || name.StartsWith("recovery"))
                return "⚠️ 重要: 启动/恢复分区";

            if (name.StartsWith("vbmeta"))
                return "⚠️ 重要: 验证启动元数据";

            if (name.StartsWith("userdata"))
                return "⚠️ 用户数据分区，刷入将清除所有数据";

            if (partition.IsLogical)
                return "逻辑分区 (需要 FastbootD 模式)";

            return "";
        }
    }

    public class FastbootDeviceInfo
    {
        public string Serial { get; set; }
        public bool IsFastbootD { get; set; }
        public string Mode { get; set; }

        public override string ToString()
        {
            return $"{Serial} ({Mode})";
        }
    }

    public class FastbootPartitionInfo
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public string SizeFormatted { get; set; }
        public bool IsLogical { get; set; }
        public string Type { get; set; }
        public bool IsCow { get; set; }
    }

    /// <summary>
    /// 列表过滤器辅助类 (借鉴 FastbootEnhance 的 ListHelper)
    /// </summary>
    public class ListFilterHelper<T>
    {
        private readonly ListView _listView;
        private readonly List<T> _allItems;
        private readonly Func<T, ListViewItem> _itemFactory;
        private readonly Func<T, string, bool> _filterFunc;
        private string _currentFilter = "";

        public ListFilterHelper(
            ListView listView, 
            Func<T, ListViewItem> itemFactory,
            Func<T, string, bool> filterFunc)
        {
            _listView = listView;
            _allItems = new List<T>();
            _itemFactory = itemFactory;
            _filterFunc = filterFunc;
        }

        public void Clear()
        {
            _allItems.Clear();
            _listView.Items.Clear();
        }

        public void AddItem(T item)
        {
            _allItems.Add(item);
        }

        public void AddItems(IEnumerable<T> items)
        {
            _allItems.AddRange(items);
        }

        public void Render()
        {
            _listView.BeginUpdate();
            _listView.Items.Clear();

            foreach (var item in _allItems)
            {
                if (string.IsNullOrEmpty(_currentFilter) || _filterFunc(item, _currentFilter))
                {
                    _listView.Items.Add(_itemFactory(item));
                }
            }

            _listView.EndUpdate();
        }

        public void SetFilter(string filter)
        {
            _currentFilter = filter ?? "";
            Render();
        }

        public IReadOnlyList<T> GetAllItems() => _allItems.AsReadOnly();

        public int TotalCount => _allItems.Count;
        public int FilteredCount => _listView.Items.Count;
    }

    /// <summary>
    /// 任务栏进度辅助类 (借鉴 FastbootEnhance 的 TaskbarItemHelper)
    /// </summary>
    public static class TaskbarProgressHelper
    {
        // 使用 Windows 7+ 任务栏进度 API
        private static ITaskbarList3 _taskbarList;
        private static IntPtr _mainWindowHandle;
        private static bool _initialized;

        public static void Initialize(IntPtr windowHandle)
        {
            _mainWindowHandle = windowHandle;
            try
            {
                _taskbarList = (ITaskbarList3)new TaskbarList();
                _taskbarList.HrInit();
                _initialized = true;
            }
            catch
            {
                _initialized = false;
            }
        }

        public static void SetProgress(int percent)
        {
            if (!_initialized || _mainWindowHandle == IntPtr.Zero) return;

            try
            {
                _taskbarList.SetProgressValue(_mainWindowHandle, (ulong)percent, 100);
            }
            catch { }
        }

        public static void SetState(TaskbarProgressState state)
        {
            if (!_initialized || _mainWindowHandle == IntPtr.Zero) return;

            try
            {
                _taskbarList.SetProgressState(_mainWindowHandle, state);
            }
            catch { }
        }

        public static void Start()
        {
            SetState(TaskbarProgressState.Normal);
            SetProgress(0);
        }

        public static void Stop()
        {
            SetState(TaskbarProgressState.NoProgress);
        }

        public static void SetIndeterminate()
        {
            SetState(TaskbarProgressState.Indeterminate);
        }

        public static void SetError()
        {
            SetState(TaskbarProgressState.Error);
        }
    }

    public enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    // COM 接口定义
    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState state);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [System.Runtime.InteropServices.ClassInterface(System.Runtime.InteropServices.ClassInterfaceType.None)]
    internal class TaskbarList { }
}
