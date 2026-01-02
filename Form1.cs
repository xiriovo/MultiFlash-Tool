using AntdUI;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Emit;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OPFlashTool.Services;
using OPFlashTool.Qualcomm;
using OPFlashTool.FastbootEnhance;
using OPFlashTool.Authentication;
using ChromeosUpdateEngine;
using static System.ComponentModel.Design.ObjectSelectorEditor;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Button;

namespace OPFlashTool
{
    public partial class Form1 : AntdUI.Window
    {
        private string targetFolder = @"C:\MultiFlashTool";
        private string[] filesToCopy = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.exe", "fastboot.exe", "avcodec-61.dll", "avformat-61.dll", "avutil-59.dll", "icon.png", "libusb-1.0.dll", "open_a_terminal_here.bat", "scrcpy.exe", "scrcpy-console.bat", "scrcpy-noconsole.vbs", "scrcpy-server", "SDL2.dll", "swresample-5.dll", "QingLi.cmd", "Qing.cmd" };
        private readonly string[] cleanupProcessNames = { "scrcpy", "adb", "fastboot" };
        private bool _isCleanupInProgress;
        private bool _cleanupCompleted;
        private string logFilePath = "";
        private bool sessionLogFileInitialized = false;
        private System.Windows.Forms.Timer detectionTimer = null!;
        private CancellationTokenSource _cts = null!;
        // 云端功能已移除 - 相关字段已清理
        private readonly DeviceManager deviceManager = new DeviceManager();

        private readonly List<LogEntry> logHistory = new List<LogEntry>();
        private bool hasPendingOperation;
        private int pendingOperationLogIndex = -1;

        private bool isDeviceRebooting;
        private bool detectionCancellationRequested;
        private int detectionWorkInProgress;
        private const string DeviceStatusPrefix = "设备状态：";

        private ListViewItem? lastHighlightedItem;
        private Color lastHighlightBackColor = Color.Empty;
        private Color lastHighlightForeColor = Color.Empty;
        private readonly Color partitionHighlightColor = Color.FromArgb(64, 158, 255);
        private readonly Color partitionHighlightTextColor = Color.White;

        private bool _isOperationInProgress;
        private bool isOriginalLayoutSaved;
        private Point originalInput5Location;
        private Point originalButton2Location;
        private Point originalCheckbox2Location;
        private Size originalListView1Size;
        private Point originalListView1Location;
        private Size originalInput7Size;
        private Point originalInput7Location;

        private string currentFirmwareFolder = string.Empty;
        private readonly List<string> currentPatchFiles = new List<string>();
        private const string LocalLoaderOption = "自选本地引导";
        private const string CloudInputBlockedMessage = "云端功能已禁用";

        private readonly HashSet<string> protectedPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "modem", "modemst1", "modemst2", "fsg", "persist", "persistbak",
            "xbl", "xblbak", "abl", "ablbak", "tz", "tzbak", "rpm", "rpmbak",
            "cmnlib", "cmnlibbak", "cmnlib64", "cmnlib64bak", "devcfg", "devcfgbak",
            "keymaster", "keymasterbak", "hyp", "hypbak", "storsec"
        };

        #region 分区风险等级和颜色定义

        /// <summary>
        /// 分区风险等级
        /// </summary>
        private enum PartitionRiskLevel
        {
            Critical,    // 🔴 严重危险 - 损坏将变砖 (xbl, tz, hyp, rpm 等)
            Dangerous,   // 🟠 危险 - 可能无法开机 (modem, fsg, persist)
            Important,   // 🟡 重要 - 系统关键 (boot, recovery, vbmeta)
            System,      // 🔵 系统 - 系统分区 (system, vendor, product)
            UserData,    // 🟣 用户数据 - 用户资料 (userdata, cache)
            Partition,   // ⚫ 分区表 - GPT 相关
            Normal       // ⚪ 普通 - 其他分区
        }

        /// <summary>
        /// 分区风险等级颜色映射 (行背景色)
        /// </summary>
        private static readonly Dictionary<PartitionRiskLevel, Color> PartitionRiskBackColors = new Dictionary<PartitionRiskLevel, Color>
        {
            [PartitionRiskLevel.Critical] = Color.FromArgb(255, 230, 230),    // 淡红色背景
            [PartitionRiskLevel.Dangerous] = Color.FromArgb(255, 243, 224),   // 淡橙色背景
            [PartitionRiskLevel.Important] = Color.FromArgb(255, 253, 231),   // 淡黄色背景
            [PartitionRiskLevel.System] = Color.FromArgb(227, 242, 253),      // 淡蓝色背景
            [PartitionRiskLevel.UserData] = Color.FromArgb(243, 229, 245),    // 淡紫色背景
            [PartitionRiskLevel.Partition] = Color.FromArgb(232, 232, 232),   // 淡灰色背景
            [PartitionRiskLevel.Normal] = Color.White                         // 白色背景
        };

        /// <summary>
        /// 分区风险等级文字颜色映射
        /// </summary>
        private static readonly Dictionary<PartitionRiskLevel, Color> PartitionRiskForeColors = new Dictionary<PartitionRiskLevel, Color>
        {
            [PartitionRiskLevel.Critical] = Color.FromArgb(183, 28, 28),      // 深红色
            [PartitionRiskLevel.Dangerous] = Color.FromArgb(230, 81, 0),      // 深橙色
            [PartitionRiskLevel.Important] = Color.FromArgb(245, 127, 23),    // 琥珀色
            [PartitionRiskLevel.System] = Color.FromArgb(21, 101, 192),       // 蓝色
            [PartitionRiskLevel.UserData] = Color.FromArgb(106, 27, 154),     // 紫色
            [PartitionRiskLevel.Partition] = Color.FromArgb(66, 66, 66),      // 深灰色
            [PartitionRiskLevel.Normal] = Color.Black                         // 黑色
        };

        /// <summary>
        /// 严重危险分区 - 损坏将导致设备变砖，无法通过软件修复
        /// </summary>
        private static readonly HashSet<string> CriticalPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Qualcomm 启动链
            "xbl", "xbl_a", "xbl_b", "xblbak", "xbl_config", "xbl_config_a", "xbl_config_b",
            "pbl", "sbl1", "sbl1bak",
            // TrustZone 安全分区
            "tz", "tz_a", "tz_b", "tzbak",
            // Hypervisor
            "hyp", "hyp_a", "hyp_b", "hypbak",
            // 电源管理
            "rpm", "rpm_a", "rpm_b", "rpmbak",
            // 安全启动
            "keymaster", "keymaster_a", "keymaster_b", "keymasterbak",
            "cmnlib", "cmnlib_a", "cmnlib_b", "cmnlibbak",
            "cmnlib64", "cmnlib64_a", "cmnlib64_b", "cmnlib64bak",
            "devcfg", "devcfg_a", "devcfg_b", "devcfgbak",
            // 存储安全
            "storsec", "storsec_a", "storsec_b",
            // DDR 配置
            "ddr", "cdt", "limits", "limits-cdsp",
            // 早期阶段分区
            "aop", "aop_a", "aop_b", "aopbak",
            "qupfw", "qupfw_a", "qupfw_b",
            "uefi", "uefi_a", "uefi_b",
            "uefisecapp", "uefisecapp_a", "uefisecapp_b",
            // OPPO/Realme/OnePlus 特殊
            "DRIVER", "oplusreserve1", "oplusreserve2"
        };

        /// <summary>
        /// 危险分区 - 损坏可能导致无法开机或丢失重要功能
        /// </summary>
        private static readonly HashSet<string> DangerousPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 基带/调制解调器
            "modem", "modem_a", "modem_b", "mdm", "mdm_a", "mdm_b",
            "modemst1", "modemst2", "fsg", "fsc",
            // DSP 处理器
            "dsp", "dsp_a", "dsp_b",
            "cdsp", "cdsp_a", "cdsp_b",
            "adsp", "adsp_a", "adsp_b",
            // ABL (应用启动加载器)
            "abl", "abl_a", "abl_b", "ablbak",
            // 持久化数据
            "persist", "persistbak", "persist_a", "persist_b",
            // 蓝牙/WiFi 固件
            "bluetooth", "bluetooth_a", "bluetooth_b",
            // IMEI/EFS
            "efs", "efs1", "efs2", "efsc", "efsg",
            // 安全相关
            "sec", "ssd", "devinfo",
            // 小米特殊
            "cust", "cust_a", "cust_b"
        };

        /// <summary>
        /// 重要系统分区 - 损坏可能导致系统无法启动
        /// </summary>
        private static readonly HashSet<string> ImportantPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 启动镜像
            "boot", "boot_a", "boot_b",
            "recovery", "recovery_a", "recovery_b",
            "init_boot", "init_boot_a", "init_boot_b",
            "vendor_boot", "vendor_boot_a", "vendor_boot_b",
            // AVB 验证
            "vbmeta", "vbmeta_a", "vbmeta_b",
            "vbmeta_system", "vbmeta_system_a", "vbmeta_system_b",
            "vbmeta_vendor", "vbmeta_vendor_a", "vbmeta_vendor_b",
            // 设备树
            "dtbo", "dtbo_a", "dtbo_b",
            // Splash
            "splash", "logo", "logo_a", "logo_b",
            // Misc
            "misc",
            // FRP
            "frp", "config"
        };

        /// <summary>
        /// 系统分区 - 操作系统核心
        /// </summary>
        private static readonly HashSet<string> SystemPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Android 系统
            "system", "system_a", "system_b",
            "system_ext", "system_ext_a", "system_ext_b",
            "vendor", "vendor_a", "vendor_b",
            "product", "product_a", "product_b",
            "odm", "odm_a", "odm_b",
            // 动态分区容器
            "super",
            // OEM
            "oem", "oem_a", "oem_b",
            "oppo", "oneplus", "my_product"
        };

        /// <summary>
        /// 用户数据分区
        /// </summary>
        private static readonly HashSet<string> UserDataPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "userdata", "cache", "metadata"
        };

        /// <summary>
        /// GPT 分区表相关
        /// </summary>
        private static readonly HashSet<string> GptPartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpt", "gpt_main0", "gpt_main1", "gpt_backup0", "gpt_backup1",
            "PrimaryGPT", "BackupGPT", "gpt_both0", "gpt_both1",
            "ALIGN_TO_128K_1", "ALIGN_TO_128K_2", "grow"
        };

        /// <summary>
        /// 获取分区的风险等级
        /// </summary>
        private PartitionRiskLevel GetPartitionRiskLevel(string partitionName)
        {
            if (string.IsNullOrEmpty(partitionName))
                return PartitionRiskLevel.Normal;

            // 检查 GPT
            if (GptPartitions.Contains(partitionName) || 
                partitionName.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0)
                return PartitionRiskLevel.Partition;

            // 检查严重危险
            if (CriticalPartitions.Contains(partitionName))
                return PartitionRiskLevel.Critical;

            // 检查危险
            if (DangerousPartitions.Contains(partitionName))
                return PartitionRiskLevel.Dangerous;

            // 检查重要
            if (ImportantPartitions.Contains(partitionName))
                return PartitionRiskLevel.Important;

            // 检查系统
            if (SystemPartitions.Contains(partitionName))
                return PartitionRiskLevel.System;

            // 检查用户数据
            if (UserDataPartitions.Contains(partitionName))
                return PartitionRiskLevel.UserData;

            return PartitionRiskLevel.Normal;
        }

        /// <summary>
        /// 获取分区风险等级的中文描述
        /// </summary>
        private string GetRiskLevelDescription(PartitionRiskLevel level)
        {
            return level switch
            {
                PartitionRiskLevel.Critical => "🔴 严重危险",
                PartitionRiskLevel.Dangerous => "🟠 危险",
                PartitionRiskLevel.Important => "🟡 重要",
                PartitionRiskLevel.System => "🔵 系统",
                PartitionRiskLevel.UserData => "🟣 用户数据",
                PartitionRiskLevel.Partition => "⚫ 分区表",
                _ => "⚪ 普通"
            };
        }

        #endregion

        private bool isGptRead;
        private bool hasXmlPartitions; // 从 XML 加载了分区数据
        private static readonly string SystemDirectory = Environment.SystemDirectory;
        private bool _result;

        private const string FastbootOptionAutoReboot = "auto_reboot";
        private const string FastbootOptionSwitchSlotA = "switch_slot_a";
        private const string FastbootOptionKeepData = "keep_data";
        private const string FastbootOptionLockBootloader = "lock_bootloader";
        private const string FastbootOptionEraseFrp = "erase_frp";

        private Dictionary<string, AntdUI.Checkbox> fastbootOptionCheckboxes = null!;
        private readonly Dictionary<string, bool> fastbootOptionUserOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> fastbootOptionDefaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [FastbootOptionAutoReboot] = false,
            [FastbootOptionSwitchSlotA] = false,
            [FastbootOptionKeepData] = true,
            [FastbootOptionLockBootloader] = false,
            [FastbootOptionEraseFrp] = false
        };
        private bool suppressListView2CheckEvents;
        private bool suppressFastbootOptionEvents;
        private bool suppressFastbootSelectAllEvent;
        private bool suppressSelect4Events;
        private string fastbootSearchText = string.Empty;

        private Payload? currentPayload;

        // ============== Fastboot 增强功能 (借鉴 libxzr/FastbootEnhance) ==============
        private FastbootService _fastbootService;
        private FastbootUIHelper _fastbootUIHelper;
        private FastbootDeviceData _currentFastbootData;
        private PayloadFlasher _payloadFlasher;
        private LogicalPartitionManager _logicalPartitionManager;
        private VabManager _vabManager;

        /// <summary>
        /// 初始化 Fastboot 增强服务
        /// </summary>
        private void InitializeFastbootEnhancedServices()
        {
            _fastbootService = new FastbootService(FastbootPath);
            _fastbootService.OnLog += (msg) => AppendFastbootLog(msg);
            _fastbootService.OnProgress += (percent) => 
            {
                this.BeginInvoke(new Action(() =>
                {
                    // 可以更新进度条
                }));
            };
            _fastbootService.OnDeviceDataLoaded += (data) =>
            {
                _currentFastbootData = data;
                this.BeginInvoke(new Action(() => UpdateFastbootDeviceInfo(data)));
            };

            _fastbootUIHelper = new FastbootUIHelper(_fastbootService, AppendFastbootLog, 
                (msg, color) => AppendLog(msg, color));
            _fastbootUIHelper.OnDevicesChanged += (devices) =>
            {
                // 设备列表变化时更新 UI
            };
            _fastbootUIHelper.OnDeviceDataLoaded += (data) =>
            {
                _currentFastbootData = data;
            };

            _payloadFlasher = new PayloadFlasher(_fastbootService);
            _payloadFlasher.OnLog += (msg) => AppendFastbootLog(msg);
            _payloadFlasher.OnProgress += (current, total, partition) =>
            {
                this.BeginInvoke(new Action(() =>
                {
                    var percent = total > 0 ? (current * 100 / total) : 0;
                    AppendFastbootLog($"[{current}/{total}] {partition} ({percent}%)");
                }));
            };

            // 初始化任务栏进度
            TaskbarProgressHelper.Initialize(this.Handle);
        }

        /// <summary>
        /// 更新 Fastboot 设备信息显示
        /// </summary>
        private void UpdateFastbootDeviceInfo(FastbootDeviceData data)
        {
            if (data == null) return;

            // 显示设备信息
            AppendFastbootLog("========== 设备信息 ==========");
            AppendFastbootLog($"设备型号: {data.Product ?? "Unknown"}");
            AppendFastbootLog($"序列号: {data.SerialNo ?? "Unknown"}");
            AppendFastbootLog($"安全启动: {(data.Secure ? "已启用" : "已禁用")}");
            AppendFastbootLog($"Bootloader: {(data.Unlocked ? "已解锁" : "已锁定")}");
            
            if (data.HasSlot)
            {
                AppendFastbootLog($"A/B 分区: 是 (当前槽位: {data.CurrentSlot?.ToUpper()})");
            }
            
            AppendFastbootLog($"FastbootD 模式: {(data.IsFastbootD ? "是" : "否")}");
            
            if (data.MaxDownloadSize > 0)
            {
                AppendFastbootLog($"最大下载: {FastbootDeviceData.FormatSize(data.MaxDownloadSize)}");
            }

            // VAB 状态检查
            var vabStatus = VabManager.ParseStatus(data.SnapshotUpdateStatus);
            if (vabStatus != VabManager.VabStatus.None && vabStatus != VabManager.VabStatus.Unknown)
            {
                AppendFastbootLog($"⚠ VAB 状态: {VabManager.GetStatusDescription(vabStatus)}");
            }

            if (data.HasCowPartitions)
            {
                AppendFastbootLog($"⚠ 检测到 {data.CowPartitions.Count} 个 COW 分区");
            }

            AppendFastbootLog($"分区总数: {data.PartitionSizes.Count}");
            AppendFastbootLog($"逻辑分区: {data.LogicalPartitions.Count}");
            AppendFastbootLog("================================");
        }

        /// <summary>
        /// 检查 VAB 状态并警告用户
        /// </summary>
        private async Task<bool> CheckVabStatusAndWarnAsync()
        {
            if (_currentFastbootData == null) return true;

            var (shouldWarn, message, level) = VabManager.CheckVabStatus(_currentFastbootData);
            
            if (!shouldWarn) return true;

            var title = level == VabWarningLevel.Critical ? "⚠️ 危险警告" : "⚠️ 警告";
            var icon = level == VabWarningLevel.Critical ? MessageBoxIcon.Error : MessageBoxIcon.Warning;

            var result = MessageBox.Show(
                message + "\n\n是否继续?",
                title,
                MessageBoxButtons.YesNo,
                icon);

            return result == DialogResult.Yes;
        }

        /// <summary>
        /// 检查 vbmeta 分区并询问用户
        /// </summary>
        private (bool proceed, bool disableVerity) CheckVbmetaPartition(string partitionName)
        {
            if (!VbmetaHandler.IsVbmetaPartition(partitionName))
            {
                return (true, false);
            }

            var result = MessageBox.Show(
                VbmetaHandler.GetWarningMessage() + "\n\n点击 '是' 禁用验证，点击 '否' 正常刷入",
                "vbmeta 分区检测",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            return (true, result == DialogResult.Yes);
        }

        /// <summary>
        /// 使用增强服务读取设备信息
        /// </summary>
        private async Task<FastbootDeviceData> ReadFastbootDeviceDataEnhancedAsync()
        {
            try
            {
                AppendFastbootLog("正在读取设备信息 (增强模式)...");
                
                // 获取设备序列号
                var devices = await _fastbootService.GetDevicesAsync();
                if (devices.Count == 0)
                {
                    AppendFastbootLog("未检测到 Fastboot 设备");
                    return null;
                }

                var serial = devices[0].serial;
                AppendFastbootLog($"检测到设备: {serial} ({(devices[0].isFastbootD ? "FastbootD" : "Fastboot")})");

                // 连接并加载数据
                var connected = await _fastbootService.ConnectAsync(serial);
                if (!connected)
                {
                    AppendFastbootLog("连接设备失败");
                    return null;
                }

                _currentFastbootData = _fastbootService.DeviceData;

                // 初始化 VAB 和逻辑分区管理器
                if (_currentFastbootData != null)
                {
                    _vabManager = new VabManager(FastbootPath, serial);
                    _logicalPartitionManager = new LogicalPartitionManager(FastbootPath, serial, _currentFastbootData);
                }

                return _currentFastbootData;
            }
            catch (Exception ex)
            {
                AppendFastbootLog($"读取设备信息失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 使用增强服务刷入分区 (支持 vbmeta 特殊处理)
        /// </summary>
        private async Task<bool> FlashPartitionEnhancedAsync(string partition, string imagePath)
        {
            if (_fastbootService == null || !_fastbootService.IsConnected)
            {
                AppendFastbootLog("设备未连接");
                return false;
            }

            // 检查 VAB 状态
            if (!await CheckVabStatusAndWarnAsync())
            {
                return false;
            }

            // 检查 vbmeta
            var (proceed, disableVerity) = CheckVbmetaPartition(partition);
            if (!proceed) return false;

            var (success, message) = await _fastbootService.FlashPartitionAsync(
                partition, imagePath, disableVerity, disableVerity);

            return success;
        }

        /// <summary>
        /// Payload 直接刷入 (FastbootD 模式)
        /// </summary>
        private async Task FlashPayloadDirectAsync(string payloadPath)
        {
            if (_payloadFlasher == null)
            {
                AppendFastbootLog("Payload 刷入服务未初始化");
                return;
            }

            // 检查 VAB 状态
            if (!await CheckVabStatusAndWarnAsync())
            {
                return;
            }

            // 询问是否禁用 vbmeta 验证
            var disableVbmeta = MessageBox.Show(
                "是否禁用 vbmeta 验证?\n\n" +
                "• 刷入第三方 ROM 时建议禁用\n" +
                "• 官方固件建议保持启用",
                "vbmeta 设置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) == DialogResult.Yes;

            TaskbarProgressHelper.Start();

            var result = await _payloadFlasher.FlashPayloadAsync(
                payloadPath,
                selectedPartitions: null, // 刷入所有分区
                ignoreUnknownPartitions: true,
                ignoreChecks: false,
                disableVbmetaVerity: disableVbmeta);

            TaskbarProgressHelper.Stop();

            if (result.Success)
            {
                MessageBox.Show(
                    $"Payload 刷入完成!\n\n" +
                    $"成功: {result.SuccessfulPartitions.Count} 个分区\n" +
                    $"失败: {result.FailedPartitions.Count} 个分区",
                    "完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Payload 刷入失败!\n\n{result.ErrorMessage}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 创建逻辑分区 (仅 FastbootD)
        /// </summary>
        private async Task CreateLogicalPartitionAsync()
        {
            if (_logicalPartitionManager == null || !_logicalPartitionManager.IsSupported)
            {
                MessageBox.Show("逻辑分区操作仅支持 FastbootD 模式", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new Form
            {
                Text = "创建逻辑分区",
                Size = new Size(350, 180),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblName = new System.Windows.Forms.Label { Text = "分区名称:", Location = new Point(20, 20), AutoSize = true };
            var txtName = new System.Windows.Forms.TextBox { Location = new Point(100, 17), Width = 200 };
            var lblSize = new System.Windows.Forms.Label { Text = "大小 (字节):", Location = new Point(20, 55), AutoSize = true };
            var txtSize = new System.Windows.Forms.TextBox { Location = new Point(100, 52), Width = 200 };
            var btnOk = new System.Windows.Forms.Button { Text = "创建", Location = new Point(100, 95), Width = 80, DialogResult = DialogResult.OK };
            var btnCancel = new System.Windows.Forms.Button { Text = "取消", Location = new Point(200, 95), Width = 80, DialogResult = DialogResult.Cancel };

            dialog.Controls.AddRange(new Control[] { lblName, txtName, lblSize, txtSize, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                if (string.IsNullOrWhiteSpace(txtName.Text))
                {
                    MessageBox.Show("分区名称不能为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!long.TryParse(txtSize.Text, out var size) || size <= 0)
                {
                    MessageBox.Show("请输入有效的分区大小", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var (success, message) = await _logicalPartitionManager.CreatePartitionAsync(txtName.Text.Trim(), size);
                
                if (success)
                {
                    AppendFastbootLog($"✓ {message}");
                    MessageBox.Show(message, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    // 刷新分区列表
                    await ReadFastbootDeviceDataEnhancedAsync();
                }
                else
                {
                    AppendFastbootLog($"✗ {message}");
                    MessageBox.Show(message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// 删除逻辑分区 (仅 FastbootD)
        /// </summary>
        private async Task DeleteLogicalPartitionAsync(string partitionName)
        {
            if (_logicalPartitionManager == null || !_logicalPartitionManager.IsSupported)
            {
                MessageBox.Show("逻辑分区操作仅支持 FastbootD 模式", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除逻辑分区 '{partitionName}' 吗?\n\n此操作不可逆!",
                "确认删除",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            var (success, message) = await _logicalPartitionManager.DeletePartitionAsync(partitionName);
            
            if (success)
            {
                AppendFastbootLog($"✓ {message}");
                MessageBox.Show(message, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // 刷新分区列表
                await ReadFastbootDeviceDataEnhancedAsync();
            }
            else
            {
                AppendFastbootLog($"✗ {message}");
                MessageBox.Show(message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 取消 VAB 更新
        /// </summary>
        private async Task CancelVabUpdateAsync()
        {
            if (_vabManager == null)
            {
                MessageBox.Show("VAB 管理器未初始化", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var result = MessageBox.Show(
                "确定要取消 VAB 更新吗?\n\n" +
                "这将清除待合并的更新数据，\n" +
                "设备将保持在当前系统版本。",
                "确认取消",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            var (success, message) = await _vabManager.CancelUpdateAsync();
            
            if (success)
            {
                AppendFastbootLog($"✓ {message}");
                MessageBox.Show(message, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // 刷新设备信息
                await ReadFastbootDeviceDataEnhancedAsync();
            }
            else
            {
                AppendFastbootLog($"✗ {message}");
                MessageBox.Show(message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 切换 A/B 槽位
        /// </summary>
        private async Task SwitchSlotAsync()
        {
            if (_fastbootService == null || !_fastbootService.IsConnected)
            {
                AppendFastbootLog("设备未连接");
                return;
            }

            if (_currentFastbootData == null || !_currentFastbootData.HasSlot)
            {
                MessageBox.Show("设备不支持 A/B 分区", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var currentSlot = _currentFastbootData.CurrentSlot?.ToLowerInvariant();
            var targetSlot = currentSlot == "a" ? "b" : "a";

            var result = MessageBox.Show(
                $"确定要将活动槽位从 {currentSlot?.ToUpper()} 切换到 {targetSlot.ToUpper()} 吗?",
                "切换槽位",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            var (success, message) = await _fastbootService.SetActiveSlotAsync(targetSlot);
            
            if (success)
            {
                AppendFastbootLog($"✓ {message}");
                MessageBox.Show(message, "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                // 刷新设备信息
                await ReadFastbootDeviceDataEnhancedAsync();
            }
            else
            {
                AppendFastbootLog($"✗ {message}");
                MessageBox.Show(message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        // ============== Fastboot 增强功能结束 ==============

        /// <summary>
        /// 安全执行异步操作，统一捕获并显示异常
        /// </summary>
        private async void SafeExecuteAsync(Func<Task> action, string? operationName = null)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                var name = operationName ?? "操作";
                Debug.WriteLine($"[SafeExecuteAsync] {name} 失败: {ex}");
                AppendLog($"{name}出错: {ex.Message}", Color.Red);
            }
        }

        public Form1()
        {
            InitializeComponent();
            // 初始化日志文件路径（按日期）
            InitializeLogFilePath();
            // 注册软件关闭事件
            this.FormClosing += Form1_FormClosing;

            // 初始化基础设施
            InitializeLogDirectory();
            InitializeDetectionTimer();
            // 云端功能已移除
            InitializeFastbootOptionCheckboxes();
            InitializeListViewBehaviors();
            InitializePartitionHelpers();
            RefreshSelect4Items();
            InitializeFastbootEnhancedServices(); // Fastboot 增强服务

            // 默认 UI 状态
            checkbox8.Checked = true;
            radio1.Checked = true;
            checkbox6.Checked = true; // 默认开启保护分区

            select5.TextChanged += select5_TextChanged;

            // 云端功能已移除

            // 加载系统信息和后台初始化
            this.Load += async (sender, e) =>
            {
                try
                {
                    // 并行执行：系统信息获取 + 文件复制
                    var sysInfoTask = WindowsInfo.GetSystemInfoAsync();
                    var copyTask = Task.Run(() => CopyFilesToTargetFolder());
                    
                    label5.Text = $"系统：{await sysInfoTask}";
                    await copyTask;

                    AppendLog("加载中...OK", Color.Green);

                    // 启动时显示 Q 群邀请（使用 AntdUI 弹窗）
                    AntdUI.Modal.open(new AntdUI.Modal.Config(this, "加入官方社区", "欢迎使用 MultiFlash Tool！\n\n官方 QQ 交流群：MultiFlash TOOL\n是否立即加入群聊获取最新资讯和支持？", AntdUI.TType.Info)
                    {
                        OkText = "加入 Q 群",
                        CancelText = "暂不加入",
                        OnOk = (config) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo("https://qm.qq.com/q/oCwGmTm5a2") { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                AppendLog($"无法打开 Q 群链接: {ex.Message}", Color.Red);
                            }
                            return true;
                        }
                    });
                }
                catch (Exception ex)
                {
                    label5.Text = $"系统信息错误: {ex.Message}";
                    AppendLog($"初始化失败: {ex.Message}", Color.Red);
                }
            };

            //   InitializePartitionHelpers();
        }

        private void CopyFilesToTargetFolder()
        {
            try
            {
                EnsureTargetFolderReady();

                string applicationPath = Application.StartupPath;
                foreach (string file in filesToCopy)
                {
                    string sourceFilePath = Path.Combine(applicationPath, file);
                    string destinationFilePath = Path.Combine(targetFolder, file);

                    if (!File.Exists(sourceFilePath))
                    {
                        continue;
                    }

                    TryCopyDependency(sourceFilePath, destinationFilePath);
                }
            }
            catch (UnauthorizedAccessException uaEx)
            {
                AppendLog($"复制依赖文件失败: {uaEx.Message}", Color.Red);
                ShowErrorMessage("无法写入 C: 根目录，请使用管理员权限启动或调整文件夹访问权限。");
            }
            catch (Exception ex)
            {
                AppendLog($"复制依赖文件异常: {ex.Message}", Color.Red);
            }
        }

        private void EnsureTargetFolderReady()
        {
            if (!Directory.Exists(targetFolder))
            {
                Directory.CreateDirectory(targetFolder);
            }

            DirectoryInfo dirInfo = new DirectoryInfo(targetFolder);
            dirInfo.Attributes &= ~FileAttributes.ReadOnly;
            dirInfo.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }

        private void TryCopyDependency(string sourceFilePath, string destinationFilePath)
        {
            // 跳过不需要更新的文件（大小相同）
            if (File.Exists(destinationFilePath))
            {
                var srcInfo = new FileInfo(sourceFilePath);
                var dstInfo = new FileInfo(destinationFilePath);
                if (srcInfo.Length == dstInfo.Length)
                {
                    return; // 文件已存在且大小相同，跳过
                }
            }

            const int MaxAttempts = 2;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(destinationFilePath))
                    {
                        EnsureFileWritable(destinationFilePath);
                    }

                    File.Copy(sourceFilePath, destinationFilePath, true);
                    return;
                }
                catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
                {
                    EnsureFileWritable(destinationFilePath);
                }
                catch (IOException)
                {
                    return; // 静默失败，不阻塞启动
                }
            }
        }

        private void EnsureFileWritable(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return;
                }

                FileAttributes attributes = File.GetAttributes(filePath);
                FileAttributes clearedAttributes = attributes & ~(FileAttributes.ReadOnly | FileAttributes.System);
                if (clearedAttributes == 0)
                {
                    clearedAttributes = FileAttributes.Normal;
                }

                if (attributes != clearedAttributes)
                {
                    File.SetAttributes(filePath, clearedAttributes);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EnsureFileWritable 失败: {ex.Message}");
            }
        }
        private void InitializeListViewBehaviors()
        {
            if (listView1 != null)
            {
                listView1.CheckBoxes = true;
                listView1.FullRowSelect = true;
                listView1.MouseDoubleClick -= listView1_MouseDoubleClick;
                listView1.MouseDoubleClick += listView1_MouseDoubleClick;
                
                // 确保列顺序正确 (DisplayIndex)
                // 列顺序: Name, Lun, Size, Start, Sectors, FS, Fmt, File
                if (listView1.Columns.Count >= 8)
                {
                    listView1.Columns[0].DisplayIndex = 0; // Name
                    listView1.Columns[1].DisplayIndex = 1; // Lun
                    listView1.Columns[2].DisplayIndex = 2; // Size
                    listView1.Columns[3].DisplayIndex = 3; // Start
                    listView1.Columns[4].DisplayIndex = 4; // Sectors
                    listView1.Columns[5].DisplayIndex = 5; // FS
                    listView1.Columns[6].DisplayIndex = 6; // Fmt
                    listView1.Columns[7].DisplayIndex = 7; // File
                }
            }

            if (listView2 != null)
            {
                listView2.CheckBoxes = true;
                listView2.FullRowSelect = true;
                listView2.ItemChecked -= listView2_ItemChecked;
                listView2.ItemChecked += listView2_ItemChecked;
                listView2.MouseDoubleClick -= listView2_MouseDoubleClick;
                listView2.MouseDoubleClick += listView2_MouseDoubleClick;
            }

            if (checkbox2 != null)
            {
                checkbox2.CheckedChanged -= checkbox2_CheckedChanged;
                checkbox2.CheckedChanged += checkbox2_CheckedChanged;
            }
        }

        private void InitializePartitionHelpers()
        {
            if (input2 != null)
            {
                input2.DoubleClick -= Input2_DoubleClick;
                input2.DoubleClick += Input2_DoubleClick;
            }

            if (input3 != null)
            {
                input3.DoubleClick -= Input3_DoubleClick;
                input3.DoubleClick += Input3_DoubleClick;
            }

            if (input4 != null)
            {
                input4.DoubleClick -= Input4_DoubleClick;
                input4.DoubleClick += Input4_DoubleClick;
            }
        }

        private class LogEntry
        {
            public string Timestamp { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public Color Color { get; set; } = Color.White;
            public int StartPosition { get; set; } = -1;
            public int Length { get; set; } = -1;
            public bool AddNewLine { get; set; } = true;
        }

        private void InitializeLogDirectory()
        {
            string logDir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
        private async void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_cleanupCompleted)
            {
                return;
            }

            e.Cancel = true;
            if (_isCleanupInProgress)
            {
                return;
            }

            _isCleanupInProgress = true;

            try
            {
                try
                {
                    detectionCancellationRequested = true;
                    detectionTimer?.Stop();
                    detectionTimer?.Dispose();

                    var spinStart = Environment.TickCount;
                    while (Interlocked.CompareExchange(ref detectionWorkInProgress, 0, 0) == 1 && Environment.TickCount - spinStart < 2000)
                    {
                        await Task.Delay(50);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FormClosing 清理准备失败: {ex.Message}");
                }

                await CleanupTargetFolderAsync();
            }
            finally
            {
                _cleanupCompleted = true;
                _isCleanupInProgress = false;
            }

            try
            {
                Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task CleanupTargetFolderAsync()
        {
            try
            {
                await KillCleanupProcessesAsync();
                await RunCleanupScriptAsync();
                await Task.Delay(500); // 给脚本一点时间释放句柄
                await DeleteTargetFolderWithRetryAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理目录失败: {ex.Message}");
            }
        }

        private async Task KillCleanupProcessesAsync()
        {
            foreach (string processName in cleanupProcessNames)
            {
                try
                {
                    foreach (Process process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                        catch (Exception killEx)
                        {
                            Debug.WriteLine($"结束进程 {processName} 失败: {killEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"查询进程 {processName} 失败: {ex.Message}");
                }
            }

            await Task.Delay(200);
        }

        private async Task RunCleanupScriptAsync()
        {
            string cleanupScriptPath = Path.Combine(targetFolder, "QingLi.cmd");
            if (!File.Exists(cleanupScriptPath))
            {
                return;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C \"\"{cleanupScriptPath}\"\"",
                    WorkingDirectory = targetFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process cleanupProcess = Process.Start(startInfo))
                {
                    if (cleanupProcess != null)
                    {
                        await Task.Run(() => cleanupProcess.WaitForExit(5000));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行 QingLi.cmd 失败: {ex.Message}");
            }
        }

        private async Task DeleteTargetFolderWithRetryAsync(int maxAttempts = 5)
        {
            if (!Directory.Exists(targetFolder))
            {
                return;
            }

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    ResetDirectoryAttributes(targetFolder);
                    Directory.Delete(targetFolder, true);
                    return;
                }
                catch (IOException ioEx)
                {
                    Debug.WriteLine($"删除目录失败（第{attempt}次）: {ioEx.Message}");
                }
                catch (UnauthorizedAccessException authEx)
                {
                    Debug.WriteLine($"删除目录权限不足（第{attempt}次）: {authEx.Message}");
                }

                await Task.Delay(500);
            }

            Debug.WriteLine($"多次尝试后仍无法删除 {targetFolder}。");
        }

        private void ResetDirectoryAttributes(string directoryPath)
        {
            try
            {
                DirectoryInfo rootInfo = new DirectoryInfo(directoryPath);
                rootInfo.Attributes &= ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);

                foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"重置文件属性失败: {ex.Message}");
                    }
                }

                foreach (string subDir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(subDir);
                        dirInfo.Attributes &= ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"重置子目录属性失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重置目录属性失败: {ex.Message}");
            }
        }
        #region 日志功能
        
        // 黑色：一般日志
        private void LogNormal(string msg) => AppendLog(msg, Color.Black);
        // 红色：错误日志
        private void LogError(string msg) => AppendLog(msg, Color.Red);
        // 绿色：成功日志
        private void LogSuccess(string msg) => AppendLog(msg, Color.Green);
        // 黄色(橙色)：警告日志 (纯黄看不清，使用橙色)
        private void LogWarning(string msg) => AppendLog(msg, Color.Orange);
        // 蓝色：信息/操作日志
        private void LogInfo(string msg) => AppendLog(msg, Color.Blue);

        #region 增强格式化日志

        // 日志颜色定义
        private static readonly Color LogColorOk = Color.FromArgb(46, 125, 50);       // 深绿色
        private static readonly Color LogColorFail = Color.FromArgb(198, 40, 40);     // 深红色
        private static readonly Color LogColorWait = Color.FromArgb(30, 136, 229);    // 蓝色
        private static readonly Color LogColorSection = Color.FromArgb(156, 39, 176); // 紫色
        private static readonly Color LogColorDevice = Color.FromArgb(0, 121, 107);   // 青色
        private static readonly Color LogColorValue = Color.FromArgb(33, 33, 33);     // 深灰色
        private static readonly Color LogColorCritical = Color.FromArgb(183, 28, 28); // 红色(严重)
        private static readonly Color LogColorDanger = Color.FromArgb(230, 81, 0);    // 橙色(危险)

        /// <summary>
        /// 输出带状态的操作日志 (action :Ok 或 :Failed)
        /// </summary>
        private void LogStatus(string action, bool success, string details = null)
        {
            string status = success ? ":Ok" : ":Failed";
            string message = string.IsNullOrEmpty(details) ? $"{action} {status}" : $"{action} {status} ({details})";
            AppendLog(message, success ? LogColorOk : LogColorFail);
        }

        /// <summary>
        /// 输出等待状态日志 (action ...)
        /// </summary>
        private void LogWaiting(string action, string details = null)
        {
            string message = string.IsNullOrEmpty(details) ? $"{action} ..." : $"{action} ({details})...";
            AppendLog(message, LogColorWait);
        }

        /// <summary>
        /// 输出章节标题 (▶️ title)
        /// </summary>
        private void LogSection(string title)
        {
            AppendLog($"▶️ {title}", LogColorSection);
        }

        /// <summary>
        /// 输出键值对日志 (• key : value)
        /// </summary>
        private void LogKeyValue(string key, string value, bool indent = true)
        {
            string prefix = indent ? "  • " : "• ";
            AppendLog($"{prefix}{key} : {value}", LogColorValue);
        }

        /// <summary>
        /// 输出设备信息项
        /// </summary>
        private void LogDeviceInfo(string label, string value)
        {
            AppendLog($"  {label,-16} :{value}", LogColorDevice);
        }

        /// <summary>
        /// 输出分隔线
        /// </summary>
        private void LogSeparator(char ch = '═', int length = 50)
        {
            AppendLog(new string(ch, length), Color.Gray);
        }

        /// <summary>
        /// 输出分区操作日志（带风险等级颜色）
        /// </summary>
        private void LogPartitionOperation(string operation, string partitionName, bool success = true, string details = null)
        {
            var riskLevel = GetPartitionRiskLevel(partitionName);
            var riskColor = riskLevel switch
            {
                PartitionRiskLevel.Critical => LogColorCritical,
                PartitionRiskLevel.Dangerous => LogColorDanger,
                _ => success ? LogColorOk : LogColorFail
            };
            
            string riskIcon = riskLevel switch
            {
                PartitionRiskLevel.Critical => "🔴",
                PartitionRiskLevel.Dangerous => "🟠",
                PartitionRiskLevel.Important => "🟡",
                PartitionRiskLevel.System => "🔵",
                PartitionRiskLevel.UserData => "🟣",
                PartitionRiskLevel.Partition => "⚫",
                _ => "⚪"
            };

            string status = success ? ":Ok" : ":Failed";
            string message = $"[{operation}] {riskIcon} {partitionName} {status}";
            if (!string.IsNullOrEmpty(details))
                message += $" ({details})";
                
            AppendLog(message, riskColor);
        }

        /// <summary>
        /// 输出危险操作警告
        /// </summary>
        private void LogDangerWarning(string partitionName, string operation)
        {
            var riskLevel = GetPartitionRiskLevel(partitionName);
            if (riskLevel == PartitionRiskLevel.Critical)
            {
                AppendLog($"⚠️ 严重警告: 分区 [{partitionName}] 是关键启动分区，{operation}可能导致设备变砖！", LogColorCritical);
            }
            else if (riskLevel == PartitionRiskLevel.Dangerous)
            {
                AppendLog($"⚠️ 警告: 分区 [{partitionName}] 包含重要数据，{operation}可能导致功能异常！", LogColorDanger);
            }
        }

        /// <summary>
        /// 输出分区表摘要（带颜色统计）
        /// </summary>
        private void LogPartitionSummary(List<PartitionInfo> partitions)
        {
            // 统计各风险等级的分区数量
            int critical = 0, dangerous = 0, important = 0, system = 0, userData = 0, gpt = 0, normal = 0;
            
            foreach (var part in partitions)
            {
                var level = GetPartitionRiskLevel(part.Name);
                switch (level)
                {
                    case PartitionRiskLevel.Critical: critical++; break;
                    case PartitionRiskLevel.Dangerous: dangerous++; break;
                    case PartitionRiskLevel.Important: important++; break;
                    case PartitionRiskLevel.System: system++; break;
                    case PartitionRiskLevel.UserData: userData++; break;
                    case PartitionRiskLevel.Partition: gpt++; break;
                    default: normal++; break;
                }
            }

            LogSection("分区风险统计");
            if (critical > 0)
                AppendLog($"  🔴 严重危险: {critical} 个 (损坏将变砖)", LogColorCritical);
            if (dangerous > 0)
                AppendLog($"  🟠 危险: {dangerous} 个 (可能无法开机)", LogColorDanger);
            if (important > 0)
                AppendLog($"  🟡 重要: {important} 个 (系统关键)", Color.FromArgb(245, 127, 23));
            if (system > 0)
                AppendLog($"  🔵 系统: {system} 个 (操作系统)", Color.FromArgb(21, 101, 192));
            if (userData > 0)
                AppendLog($"  🟣 用户数据: {userData} 个", Color.FromArgb(106, 27, 154));
            if (gpt > 0)
                AppendLog($"  ⚫ 分区表: {gpt} 个", Color.FromArgb(66, 66, 66));
            if (normal > 0)
                AppendLog($"  ⚪ 普通: {normal} 个", Color.Black);
        }

        #endregion

        // Fastboot 日志必须走统一通道，否则会被 RebuildAllLogs 清除
        private void AppendFastbootLog(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<string>(AppendFastbootLog), message);
                return;
            }
            // 统一使用黑色作为 Fastboot 常规输出
            LogNormal($"[Fastboot] {message}");
        }
        /// <summary>
        /// 修复1: 初始化日志文件路径（每次启动生成一个新日志）
        /// </summary>
        private void InitializeLogFilePath()
        {
            try
            {
                string logDir = @"C:\MultiFlashTool_log";
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"log_{timestamp}.txt";
                logFilePath = Path.Combine(logDir, fileName);

                using (var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.WriteLine($"=== Tool日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                }

                sessionLogFileInitialized = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化日志文件失败: {ex.Message}");
                sessionLogFileInitialized = false;
            }
        }
        
        #endregion

        #region 云端功能
        
        /// <summary>
        /// 初始化菜单事件 (云端功能已移除)
        /// </summary>
        private void InitializeMenuEvents()
        {
            try
            {
                eDL重启到系统ToolStripMenuItem.Click += async (s, e) => await RunEdlCommandAsync("<data><power value=\"reset\"/></data>", "重启到系统");
                eDL到恢复模式ToolStripMenuItem.Click += async (s, e) => await RunEdlCommandAsync("<data><power value=\"reset_to_recovery\"/></data>", "重启到恢复模式");
                eDL到EDLToolStripMenuItem.Click += async (s, e) => await RunEdlCommandAsync("<data><power value=\"reset_to_edl\"/></data>", "重启到EDL");
                eDL到FBDToolStripMenuItem.Click += async (s, e) => await RunEdlCommandAsync("<data><power value=\"reset_to_fastboot\"/></data>", "重启到FastbootD");
                eDL通用恢复出厂ToolStripMenuItem.Click += async (s, e) => await RunEdlEraseAsync("userdata");
                移除FrpToolStripMenuItem.Click += async (s, e) => await RunEdlEraseAsync("frp");
                
                select1.TextChanged += Select1_TextChanged;
                // SelectedIndexChanged 已在 Designer.cs 中绑定
                InitializeAdvancedEdlMenu();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化菜单事件失败: {ex.Message}");
            }
        }
        
        private async Task RunEdlPowerCommandAsync(string mode)
        {
            if (!TryGetSerialForAction($"电源控制 ({mode})", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }
            
            AppendLog($"[Power] 执行: {mode}...", Color.Blue);
            await RunEdlOperationAsync(port, async (firehose) =>
            {
                bool success = firehose.PowerCommand(mode);
                if (success)
                    AppendLog($"[Power] 命令已发送", Color.Green);
                else
                    AppendLog("[Power] 执行失败", Color.Red);
                await Task.CompletedTask;
            });
        }
        
        /// <summary>
        /// 初始化 EDL 高级功能菜单
        /// </summary>
        private void InitializeAdvancedEdlMenu()
        {
            try
            {
                // 创建分隔线
                var separator = new ToolStripSeparator();
                eDL操作ToolStripMenuItem.DropDownItems.Add(separator);
                
                // GPT 备份
                var gptBackupItem = new ToolStripMenuItem("备份 GPT 分区表");
                gptBackupItem.Click += async (s, e) => await BackupGptAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(gptBackupItem);
                
                // GPT 恢复
                var gptRestoreItem = new ToolStripMenuItem("恢复 GPT 分区表");
                gptRestoreItem.Click += async (s, e) => await RestoreGptAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(gptRestoreItem);
                
                // 分隔线
                eDL操作ToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
                
                // 获取设备信息
                var deviceInfoItem = new ToolStripMenuItem("获取设备信息");
                deviceInfoItem.Click += async (s, e) => await GetDeviceInfoAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(deviceInfoItem);
                
                // 内存读取 (Peek)
                var peekItem = new ToolStripMenuItem("内存读取 (Peek)");
                peekItem.Click += async (s, e) => await PeekMemoryAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(peekItem);
                
                // 内存转储
                var dumpItem = new ToolStripMenuItem("内存转储 (Dump)");
                dumpItem.Click += async (s, e) => await DumpMemoryAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(dumpItem);
                
                // 分隔线
                eDL操作ToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
                
                // SHA256 校验
                var sha256Item = new ToolStripMenuItem("SHA256 校验");
                sha256Item.Click += async (s, e) => await VerifySha256Async();
                eDL操作ToolStripMenuItem.DropDownItems.Add(sha256Item);
                
                // 设置传输窗口
                var windowItem = new ToolStripMenuItem("设置传输窗口");
                windowItem.Click += async (s, e) => await SetTransferWindowAsync();
                eDL操作ToolStripMenuItem.DropDownItems.Add(windowItem);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化高级菜单失败: {ex.Message}");
            }
        }
        
        #endregion

        #region EDL 高级功能
        
        private async Task BackupGptAsync()
        {
            if (!TryGetSerialForAction("备份GPT", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            using (var sfd = new SaveFileDialog { Filter = "二进制文件|*.bin", FileName = "gpt_backup.bin" })
            {
                if (sfd.ShowDialog() != DialogResult.OK) return;
                
                AppendLog("[GPT] 开始备份...", Color.Blue);
                await RunEdlOperationAsync(port, async (firehose) =>
                {
                    bool success = await firehose.BackupGptAsync(sfd.FileName, 0);
                    if (success)
                        AppendLog($"[GPT] 备份成功: {sfd.FileName}", Color.Green);
                    else
                        AppendLog("[GPT] 备份失败", Color.Red);
                });
            }
        }
        
        private async Task RestoreGptAsync()
        {
            if (!TryGetSerialForAction("恢复GPT", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            using (var ofd = new OpenFileDialog { Filter = "二进制文件|*.bin" })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                
                var result = MessageBox.Show("恢复 GPT 可能导致数据丢失，是否继续？", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes) return;
                
                AppendLog("[GPT] 开始恢复...", Color.Blue);
                await RunEdlOperationAsync(port, async (firehose) =>
                {
                    bool success = await firehose.RestoreGptAsync(ofd.FileName, 0);
                    if (success)
                        AppendLog("[GPT] 恢复成功", Color.Green);
                    else
                        AppendLog("[GPT] 恢复失败", Color.Red);
                });
            }
        }
        
        private async Task GetDeviceInfoAsync()
        {
            if (!TryGetSerialForAction("获取设备信息", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            LogSection("获取设备信息");
            await RunEdlOperationAsync(port, async (firehose) =>
            {
                // 1. 获取 Firehose 基本信息
                var info = firehose.GetDeviceInfo();
                if (info.Count > 0)
                {
                    LogSection("Firehose 配置");
                    foreach (var kv in info)
                    {
                        LogKeyValue(kv.Key, kv.Value);
                    }
                }

                // 2. 读取 GPT 分区表
                if (!isGptRead)
                {
                    LogWaiting("读取分区表");
                    var partitions = await ReadGptFromDeviceAsync(firehose);
                    if (partitions == null || partitions.Count == 0)
                    {
                        LogStatus("读取分区表", false, "无法获取分区信息");
                        return;
                    }
                    UpdatePartitionList(partitions);
                    isGptRead = true;
                }

                // 3. 使用 DeviceInfoReader 读取 Android 设备信息
                var partitionList = GetPartitionListFromListView();
                if (partitionList.Count > 0)
                {
                    LogSection("读取 Android 设备信息");
                    var reader = new Qualcomm.DeviceInfoReader(firehose, (msg) => AppendLog(msg, Color.Gray));
                    
                    try
                    {
                        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        var buildProps = await reader.ReadBuildPropsAsync(partitionList, cts.Token);
                        
                        if (buildProps != null && !string.IsNullOrEmpty(buildProps.Brand))
                        {
                            LogSection("Android 系统信息");
                            if (!string.IsNullOrEmpty(buildProps.Brand))
                                LogDeviceInfo("品牌", buildProps.Brand);
                            if (!string.IsNullOrEmpty(buildProps.Model))
                                LogDeviceInfo("型号", buildProps.Model);
                            if (!string.IsNullOrEmpty(buildProps.Device))
                                LogDeviceInfo("设备代号", buildProps.Device);
                            if (!string.IsNullOrEmpty(buildProps.Product))
                                LogDeviceInfo("产品", buildProps.Product);
                            if (!string.IsNullOrEmpty(buildProps.Manufacturer))
                                LogDeviceInfo("制造商", buildProps.Manufacturer);
                            if (!string.IsNullOrEmpty(buildProps.AndroidVersion))
                                LogDeviceInfo("Android 版本", buildProps.AndroidVersion);
                            if (!string.IsNullOrEmpty(buildProps.SdkVersion))
                                LogDeviceInfo("SDK 版本", buildProps.SdkVersion);
                            if (!string.IsNullOrEmpty(buildProps.SecurityPatch))
                                LogDeviceInfo("安全补丁", buildProps.SecurityPatch);
                            if (!string.IsNullOrEmpty(buildProps.BuildId))
                                LogDeviceInfo("Build ID", buildProps.BuildId);
                            if (!string.IsNullOrEmpty(buildProps.RomVersion))
                                LogDeviceInfo("ROM 版本", buildProps.RomVersion);
                            if (!string.IsNullOrEmpty(buildProps.Incremental))
                                LogDeviceInfo("版本号", buildProps.Incremental);
                            if (!string.IsNullOrEmpty(buildProps.BuildFingerprint))
                                LogDeviceInfo("Fingerprint", buildProps.BuildFingerprint);
                            
                            LogStatus("读取 Android 设备信息", true);
                        }
                        else
                        {
                            LogStatus("读取 Android 设备信息", false, "无法解析 build.prop");
                            LogWarning("提示: 设备可能使用了加密或不支持的文件系统");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        LogStatus("读取 Android 设备信息", false, "操作超时");
                    }
                    catch (Exception ex)
                    {
                        LogStatus("读取 Android 设备信息", false, ex.Message);
                    }
                }
                else
                {
                    LogWarning("未获取到分区列表，无法读取 Android 信息");
                }
            });
        }

        /// <summary>
        /// 从 ListView 获取当前分区列表
        /// </summary>
        private List<PartitionInfo> GetPartitionListFromListView()
        {
            var partitions = new List<PartitionInfo>();
            if (listView1.InvokeRequired)
            {
                listView1.Invoke(new Action(() =>
                {
                    foreach (ListViewItem item in listView1.Items)
                    {
                        if (item.Tag is PartitionInfo part)
                        {
                            partitions.Add(part);
                        }
                    }
                }));
            }
            else
            {
                foreach (ListViewItem item in listView1.Items)
                {
                    if (item.Tag is PartitionInfo part)
                    {
                        partitions.Add(part);
                    }
                }
            }
            return partitions;
        }

        /// <summary>
        /// 从设备读取 GPT 分区表
        /// </summary>
        private async Task<List<PartitionInfo>> ReadGptFromDeviceAsync(FirehoseClient firehose)
        {
            var partitions = new List<PartitionInfo>();
            string tempDir = Path.Combine(Path.GetTempPath(), $"gpt_{Guid.NewGuid():N}");
            
            try
            {
                Directory.CreateDirectory(tempDir);
                
                // 尝试读取所有 LUN 的 GPT
                string storageType = firehose.StorageType ?? "ufs";
                int maxLun = storageType.Equals("ufs", StringComparison.OrdinalIgnoreCase) ? 6 : 1;
                int sectorSize = firehose.SectorSize > 0 ? firehose.SectorSize : 4096;
                
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                for (int lun = 0; lun < maxLun; lun++)
                {
                    string gptPath = Path.Combine(tempDir, $"gpt_lun{lun}.bin");
                    
                    // 读取 GPT (前 34 个扇区)
                    bool success = await firehose.ReadPartitionChunkedAsync(
                        gptPath, "0", 34, lun.ToString(),
                        null, cts.Token, $"GPT_LUN{lun}", null, false, true);
                    
                    if (!success || !File.Exists(gptPath))
                    {
                        continue;
                    }
                    
                    // 解析 GPT
                    var parsed = GptParser.ParseGptFile(gptPath, lun);
                    foreach (var p in parsed)
                    {
                        p.Source = PartitionSource.Device;
                        p.SourceFile = $"LUN{lun}";
                        p.SectorSize = sectorSize;
                        partitions.Add(p);
                    }
                    
                    AppendLog($"  LUN{lun}: {parsed.Count} 个分区", Color.Gray);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取 GPT 失败: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
            
            return partitions;
        }
        
        private async Task PeekMemoryAsync()
        {
            if (!TryGetSerialForAction("内存读取", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            string input = Microsoft.VisualBasic.Interaction.InputBox("输入内存地址 (十六进制，如 0x80000000):", "内存读取", "0x80000000");
            if (string.IsNullOrEmpty(input)) return;
            
            string sizeInput = Microsoft.VisualBasic.Interaction.InputBox("输入读取大小 (字节):", "内存读取", "256");
            if (string.IsNullOrEmpty(sizeInput)) return;

            if (!ulong.TryParse(input.Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong address))
            {
                AppendLog("[Peek] 无效的地址格式", Color.Red);
                return;
            }
            
            if (!int.TryParse(sizeInput, out int size) || size <= 0 || size > 1024 * 1024)
            {
                AppendLog("[Peek] 无效的大小 (最大 1MB)", Color.Red);
                return;
            }

            AppendLog($"[Peek] 读取 @ 0x{address:X} ({size} bytes)...", Color.Blue);
            await RunEdlOperationAsync(port, async (firehose) =>
            {
                byte[] data = firehose.PeekMemory(address, size);
                if (data != null)
                {
                    AppendLog($"[Peek] 读取成功 ({data.Length} bytes)", Color.Green);
                    // 显示前 64 字节
                    int displayLen = Math.Min(64, data.Length);
                    AppendLog($"  HEX: {BitConverter.ToString(data, 0, displayLen).Replace("-", " ")}", Color.Black);
                }
                else
                {
                    AppendLog("[Peek] 读取失败", Color.Red);
                }
                await Task.CompletedTask;
            });
        }
        
        private async Task DumpMemoryAsync()
        {
            if (!TryGetSerialForAction("内存转储", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            string addrInput = Microsoft.VisualBasic.Interaction.InputBox("输入起始地址 (十六进制):", "内存转储", "0x80000000");
            if (string.IsNullOrEmpty(addrInput)) return;
            
            string sizeInput = Microsoft.VisualBasic.Interaction.InputBox("输入转储大小 (十六进制或十进制):", "内存转储", "0x100000");
            if (string.IsNullOrEmpty(sizeInput)) return;

            if (!ulong.TryParse(addrInput.Replace("0x", "").Replace("0X", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong startAddr))
            {
                AppendLog("[Dump] 无效的地址格式", Color.Red);
                return;
            }
            
            ulong size;
            if (sizeInput.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                ulong.TryParse(sizeInput.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out size);
            }
            else
            {
                ulong.TryParse(sizeInput, out size);
            }
            
            if (size == 0 || size > 1024 * 1024 * 100) // 最大 100MB
            {
                AppendLog("[Dump] 无效的大小 (最大 100MB)", Color.Red);
                return;
            }

            using (var sfd = new SaveFileDialog { Filter = "二进制文件|*.bin", FileName = $"dump_0x{startAddr:X}.bin" })
            {
                if (sfd.ShowDialog() != DialogResult.OK) return;
                
                AppendLog($"[Dump] 转储 0x{startAddr:X} - 0x{startAddr + size:X}...", Color.Blue);
                await RunEdlOperationAsync(port, async (firehose) =>
                {
                    bool success = await firehose.DumpMemoryAsync(sfd.FileName, startAddr, size, (c, t) =>
                    {
                        float percent = (float)c / t * 100;
                        this.Invoke(new Action(() => input8.Text = $"转储中... {percent:F1}%"));
                    });
                    
                    if (success)
                        AppendLog($"[Dump] 转储成功: {sfd.FileName}", Color.Green);
                    else
                        AppendLog("[Dump] 转储失败", Color.Red);
                });
            }
        }
        
        private async Task VerifySha256Async()
        {
            if (!TryGetSerialForAction("SHA256校验", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            // 使用当前选中的分区
            var selectedItem = listView1.SelectedItems.Count > 0 ? listView1.SelectedItems[0] : null;
            if (selectedItem == null)
            {
                AppendLog("[SHA256] 请先在分区列表中选择一个分区", Color.Orange);
                return;
            }

            AppendLog($"[SHA256] 计算分区 {selectedItem.Text} 的哈希...", Color.Blue);
            await RunEdlOperationAsync(port, async (firehose) =>
            {
                // 获取分区信息
                long startSector = long.Parse(selectedItem.SubItems[2].Text);
                long sectors = long.Parse(selectedItem.SubItems[3].Text);
                int lun = int.Parse(selectedItem.SubItems[5].Text);
                
                string hash = firehose.GetSha256(lun, startSector, sectors);
                if (!string.IsNullOrEmpty(hash))
                {
                    AppendLog($"[SHA256] {selectedItem.Text}: {hash}", Color.Green);
                }
                else
                {
                    AppendLog("[SHA256] 设备不支持或计算失败", Color.Orange);
                }
                await Task.CompletedTask;
            });
        }
        
        private async Task SetTransferWindowAsync()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("输入传输窗口大小 (KB):", "传输优化", "1024");
            if (string.IsNullOrEmpty(input)) return;
            
            if (!int.TryParse(input, out int sizeKb) || sizeKb <= 0 || sizeKb > 16384)
            {
                AppendLog("[Config] 无效的大小 (1-16384 KB)", Color.Red);
                return;
            }

            if (!TryGetSerialForAction("设置传输窗口", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            await RunEdlOperationAsync(port, async (firehose) =>
            {
                bool success = firehose.SetTransferWindow(sizeKb * 1024);
                if (success)
                    AppendLog($"[Config] 传输窗口已设置为 {sizeKb}KB", Color.Green);
                else
                    AppendLog("[Config] 设置失败", Color.Red);
                await Task.CompletedTask;
            });
        }
        
        /// <summary>
        /// 通用 EDL 操作执行器 (支持自动模式检测)
        /// </summary>
        private async Task RunEdlOperationAsync(string port, Func<Qualcomm.FirehoseClient, Task> operation)
        {
            try
            {
                // 首先检测设备模式
                var mode = await DetectDeviceModeAsync(port);
                AppendLog($"[模式] 检测到: {mode}", Color.Blue);
                
                if (mode == DeviceMode.None)
                {
                    AppendLog("[错误] 未检测到设备，请确认设备已进入 9008 EDL 模式", Color.Red);
                    return;
                }
                
                if (mode == DeviceMode.Sahara)
                {
                    AppendLog("[Sahara] 设备在引导模式，需要先上传 Programmer", Color.Orange);
                }
                
                _cts = new CancellationTokenSource();
                var flasher = new Qualcomm.AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
                AuthType authType = GetAuthType();
                
                var authFiles = await PrepareAuthFilesAsync();
                if (!authFiles.ok) 
                {
                    AppendLog("[错误] 无法准备认证文件", Color.Red);
                    return;
                }
                
                var progPath = await EnsureProgrammerPathAsync();
                if (string.IsNullOrEmpty(progPath)) 
                {
                    AppendLog("[错误] 请先选择引导文件 (Programmer/Loader)", Color.Red);
                    return;
                }
                
                await flasher.RunFlashActionAsync(
                    port, progPath!, authType, checkbox4.Checked, authFiles.digest, authFiles.signature,
                    async (executor) => await operation(executor.Client),
                    _cts.Token
                );
            }
            catch (OperationCanceledException)
            {
                AppendLog("[取消] 操作已取消", Color.Orange);
            }
            catch (UnauthorizedAccessException)
            {
                AppendLog("[错误] 端口被占用，请检查是否有其他程序正在使用", Color.Red);
            }
            catch (TimeoutException)
            {
                AppendLog("[超时] 设备响应超时，请检查连接", Color.Red);
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] {ex.Message}", Color.Red);
                System.Diagnostics.Debug.WriteLine($"[EDL Error] {ex}");
            }
        }
        
        /// <summary>
        /// 设备模式枚举
        /// </summary>
        private enum DeviceMode { None, Sahara, Firehose }
        
        /// <summary>
        /// 检测设备当前模式
        /// </summary>
        private async Task<DeviceMode> DetectDeviceModeAsync(string portName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var port = new System.IO.Ports.SerialPort(portName, 115200))
                    {
                        port.ReadTimeout = 2000;
                        port.WriteTimeout = 2000;
                        port.DtrEnable = true;
                        port.RtsEnable = true;
                        
                        port.Open();
                        
                        // 清空缓冲区
                        port.DiscardInBuffer();
                        port.DiscardOutBuffer();
                        
                        // 等待数据
                        System.Threading.Thread.Sleep(500);
                        
                        if (port.BytesToRead > 0)
                        {
                            byte[] buffer = new byte[Math.Min(port.BytesToRead, 64)];
                            port.Read(buffer, 0, buffer.Length);
                            
                            // 检查 Sahara Hello 包 (0x01)
                            if (buffer.Length >= 4 && buffer[0] == 0x01 && buffer[1] == 0x00 && buffer[2] == 0x00 && buffer[3] == 0x00)
                            {
                                return DeviceMode.Sahara;
                            }
                        }
                        
                        // 尝试发送 Firehose NOP 命令
                        string nopCmd = "<?xml version=\"1.0\" ?><data><nop /></data>";
                        byte[] cmdBytes = System.Text.Encoding.UTF8.GetBytes(nopCmd);
                        port.Write(cmdBytes, 0, cmdBytes.Length);
                        
                        System.Threading.Thread.Sleep(300);
                        
                        if (port.BytesToRead > 0)
                        {
                            byte[] response = new byte[Math.Min(port.BytesToRead, 256)];
                            port.Read(response, 0, response.Length);
                            string respStr = System.Text.Encoding.UTF8.GetString(response);
                            
                            // Firehose 会返回 XML 响应
                            if (respStr.Contains("<response") || respStr.Contains("<data") || respStr.Contains("ACK") || respStr.Contains("NAK"))
                            {
                                return DeviceMode.Firehose;
                            }
                        }
                        
                        // 如果没有响应但端口打开成功，可能在 Sahara 等待
                        return DeviceMode.Sahara;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // 端口可能被其他程序占用
                    return DeviceMode.None;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DetectMode] {ex.Message}");
                    return DeviceMode.None;
                }
            });
        }

        #endregion

        private void UpdateCloudLoaderUiState()
        {
            bool cloudMode = IsCloudLoaderMode();

            if (input2 != null) input2.Enabled = !cloudMode;
            if (input3 != null) input3.Enabled = !cloudMode;
            if (input4 != null) input4.Enabled = !cloudMode;
            UpdateCloudInputDisplays();
        }

        private bool IsCloudLoaderMode()
        {
            // 云端功能已移除
            return false;
        }

        private void UpdateCloudInputDisplays()
        {
            // 空实现
        }

        private void UpdateInputDisplayForMode(AntdUI.Input input, bool showBlockedHint)
        {
            // 空实现，或根据需要保留基础逻辑
            if (input == null) return;
            string stored = input.Tag as string ?? string.Empty;
            input.Text = stored;
        }

        private void SetInputStoredPath(AntdUI.Input input, string? path, bool forceRefresh = true)
        {
            if (input == null) return;
            input.Tag = string.IsNullOrWhiteSpace(path) ? null : path;
            if (forceRefresh)
            {
                UpdateInputDisplayForMode(input, input == input2);
            }
        }

        private string? GetInputStoredPath(AntdUI.Input input)
        {
            if (input == null) return null;
            if (input.Tag is string stored && !string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }

            string text = input.Text;
            if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, CloudInputBlockedMessage, StringComparison.Ordinal))
            {
                return text;
            }

            return null;
        }

        private bool InputHasStoredPath(AntdUI.Input input)
        {
            return !string.IsNullOrWhiteSpace(GetInputStoredPath(input));
        }

        private string? GetSelectedCloudChipName()
        {
            string selected = select4?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selected) || string.Equals(selected, LocalLoaderOption, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return selected;
        }

        private void RefreshSelect4Items(string? desiredSelection = null)
        {
            if (select4 == null) return;

            suppressSelect4Events = true;
            try
            {
                string previousSelection = desiredSelection ?? select4.Text ?? string.Empty;
                var options = new List<string> { LocalLoaderOption };

                // 云端列表已移除

                select4.Items.Clear();
                select4.Items.AddRange(options.Cast<object>().ToArray());

                string normalizedSelection = LocalLoaderOption;

                select4.Text = normalizedSelection;
            }
            finally
            {
                suppressSelect4Events = false;
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "file";
            }

            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar.ToString(), "_");
            }
            return name;
        }

        private void select4_SelectedIndexChanged(object sender, IntEventArgs e)
        {
            if (suppressSelect4Events)
            {
                return;
            }

            UpdateCloudLoaderUiState();
            // 始终不是云端模式
            if (!IsCloudLoaderMode())
            {
                // ClearCloudLoaderSelections() 已移除
                return;
            }
        }

        /// <summary>
        /// 检查当前日志文件状态（如不存在则重新创建）
        /// </summary>
        private void CheckAndSwitchLogFile()
        {
            try
            {
                if (!sessionLogFileInitialized || string.IsNullOrEmpty(logFilePath))
                {
                    InitializeLogFilePath();
                    return;
                }

                string logDir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                if (!File.Exists(logFilePath))
                {
                    using (var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine($"=== Tool日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查日志文件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 追加日志到input组件和文件
        /// </summary>
        public void AppendLog(string message, Color color, bool addNewLine = true)
        {
            // 检查并切换日志文件（如果需要）
            CheckAndSwitchLogFile();
            
            // 清理消息：去除换行符和前后空格
            string cleanMessage = message.Replace("\r", "").Replace("\n", " ").Trim();

            // 更新UI日志
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<string, Color, bool>(UpdateUILog), cleanMessage, color, addNewLine);
            }
            else
            {
                UpdateUILog(cleanMessage, color, addNewLine);
            }
            
            // 写入文件日志
            WriteLogToFile(cleanMessage, addNewLine);
        }

        /// <summary>
        /// 写入日志到文件
        /// </summary>
        private void WriteLogToFile(string message, bool addNewLine)
        {
            try
            {
                bool finalAddNewLine = addNewLine;
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string cleanMessage = message.Trim();
                
                // 检查操作完成标记
                bool isOperationComplete = ContainsIgnoreCase(message, "...ok") || message.Contains("...Error") || message.Contains("...OK");
                bool isOperationStart = message.EndsWith("...") && !ContainsIgnoreCase(message, "ok") && !message.Contains("Error");
                bool isOperationResult = EqualsIgnoreCase(message, "ok") || message == "Error" || message.Contains("Error:");
                bool isCompleteOperation = ContainsIgnoreCase(message, "...ok") || message.Contains("...Error") || message.Contains("...OK");
                
                bool fileHasContent = File.Exists(logFilePath) && new FileInfo(logFilePath).Length > 0;
                bool lastCharWasNewLine = false;
                string lastLine = string.Empty;
                
                if (fileHasContent)
                {
                    // 读取文件的最后一个字符，判断是否是换行符
                    using (FileStream fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read))
                    {
                        if (fs.Length > 0)
                        {
                            fs.Seek(-1, SeekOrigin.End);
                            int lastChar = fs.ReadByte();
                            lastCharWasNewLine = (lastChar == 10 || lastChar == 13); // 10是LF，13是CR
                            
                            // 读取最后一行内容
                            if (!lastCharWasNewLine)
                            {
                                fs.Seek(0, SeekOrigin.Begin);
                                using (StreamReader sr = new StreamReader(fs))
                                {
                                    string line;
                                    while ((line = sr.ReadLine()) != null)
                                    {
                                        if (!string.IsNullOrWhiteSpace(line))
                                        {
                                            lastLine = line;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                // 确定是否需要添加换行符
                if (!fileHasContent)
                {
                    // 文件为空，总是添加时间戳和换行符
                    finalAddNewLine = true;
                }
                else if (!lastCharWasNewLine)
                {
                    // 如果文件没有以换行符结束，检查最后一行的内容
                    bool lastLineHasPendingOperation = lastLine.Contains("...") && !ContainsIgnoreCase(lastLine, "ok") && !lastLine.Contains("Error");
                    
                    if (isOperationResult && lastLineHasPendingOperation)
                    {
                        // 如果当前是操作结果，并且上一行有未完成的操作，不要添加换行符
                        finalAddNewLine = false;
                    }
                    else if (isOperationStart || isCompleteOperation || (!isOperationResult && !lastLineHasPendingOperation))
                    {
                        // 如果是新操作开始、完整操作或常规消息，确保添加换行符
                        finalAddNewLine = true;
                    }
                }
                else
                {
                    // 文件以换行符结束
                    if (isOperationResult)
                    {
                        // 操作结果需要检查上一行是否有未完成的操作
                        using (StreamReader sr = new StreamReader(logFilePath))
                        {
                            string line;
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    lastLine = line;
                                }
                            }
                        }
                        
                        bool lastLineHasPendingOperation = lastLine.Contains("...") && !ContainsIgnoreCase(lastLine, "ok") && !lastLine.Contains("Error");
                        if (lastLineHasPendingOperation)
                        {
                            // 如果上一行有未完成的操作，不要添加换行符
                            finalAddNewLine = false;
                        }
                        else
                        {
                            // 否则添加换行符
                            finalAddNewLine = true;
                        }
                    }
                    else if (isOperationStart || isCompleteOperation)
                    {
                        // 新操作开始或完整操作，添加换行符
                        finalAddNewLine = true;
                    }
                }
                
                using (StreamWriter sw = new StreamWriter(logFilePath, true, Encoding.UTF8))
                {
                    if (finalAddNewLine)
                    {
                        sw.WriteLine($"{timestamp} {cleanMessage}");
                    }
                    else
                    {
                        // 如果不添加新行，直接追加内容
                        sw.Write($"{cleanMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"日志文件写入失败: {ex.Message}");
            }
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(value)) return false;
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool EqualsIgnoreCase(string source, string value)
        {
            return string.Equals(source, value, StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWithIgnoreCase(string source, string value)
        {
            if (source == null || value == null) return false;
            return source.EndsWith(value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 清空UI中的日志显示和相关状态
        /// </summary>
        private void ClearLogDisplay()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(ClearLogDisplay));
                return;
            }

            hasPendingOperation = false;
            pendingOperationLogIndex = -1;
            logHistory.Clear();
            TryClearStyles();
            input1.Clear();
            input1.SelectionStart = 0;
            input1.ScrollToCaret();
        }

        /// <summary>
        /// 更新UI中的日志显示 - 使用重建方式
        /// </summary>
        private void UpdateUILog(string message, Color color, bool addNewLine = true)
        {
            try
            {
                bool isOperationStart = message.EndsWith("...") && !ContainsIgnoreCase(message, "ok") && !message.Contains("Error");
                bool isOperationResult = EqualsIgnoreCase(message, "ok") || message == "Error" || message.Contains("Error:");
                bool isCompleteOperation = ContainsIgnoreCase(message, "...ok") || message.Contains("...Error") || message.Contains("...OK");
                
                // 特殊处理：如果当前有未完成的操作，但接收到了新的操作开始或完整操作，先完成之前的操作
                if (hasPendingOperation && logHistory.Count > 0 && (isOperationStart || isCompleteOperation))
                {
                    var pendingLog = GetPendingOperationLog();
                    if (pendingLog != null)
                    {
                        if (!EndsWithIgnoreCase(pendingLog.Message, "ok") && !pendingLog.Message.EndsWith("Error"))
                        {
                            pendingLog.Message += "Error";
                            pendingLog.Color = Color.Red;
                        }
                        pendingLog.AddNewLine = true;
                    }
                    hasPendingOperation = false;
                    pendingOperationLogIndex = -1;
                }
                
                if (isCompleteOperation)
                {
                    // 处理完整的操作消息（如"重启到Fastboot...ok"）
                    // 确保前一个条目添加换行符
                    if (logHistory.Count > 0)
                    {
                        var lastLog = logHistory.Last();
                        lastLog.AddNewLine = true;
                    }
                    
                    // 添加新的日志条目，添加换行符
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    logHistory.Add(new LogEntry
                    {
                        Timestamp = timestamp,
                        Message = message,
                        Color = color,
                        StartPosition = -1, // 稍后计算
                        Length = -1,
                        AddNewLine = true
                    });
                    hasPendingOperation = false;
                    pendingOperationLogIndex = -1;
                }
                else if (isOperationStart)
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    logHistory.Add(new LogEntry
                    {
                        Timestamp = timestamp,
                        Message = message,
                        Color = color,
                        StartPosition = -1,
                        Length = -1,
                        AddNewLine = false
                    });

                    hasPendingOperation = true;
                    pendingOperationLogIndex = logHistory.Count - 1;
                }
                else if (isOperationResult && hasPendingOperation && logHistory.Count > 0)
                {
                    // 完成当前操作
                    hasPendingOperation = false;
                    var pendingLog = GetPendingOperationLog();
                    if (pendingLog != null)
                    {
                        pendingLog.Message += message;
                        pendingLog.Color = color;
                        pendingLog.AddNewLine = true;
                    }
                    else
                    {
                        // 如果找不到对应的操作，按常规方式记录一次
                        string timestamp = DateTime.Now.ToString("HH:mm:ss");
                        logHistory.Add(new LogEntry
                        {
                            Timestamp = timestamp,
                            Message = message,
                            Color = color,
                            StartPosition = -1,
                            Length = -1,
                            AddNewLine = true
                        });
                    }
                    pendingOperationLogIndex = -1;
                }
                else if (!addNewLine && logHistory.Count > 0)
                {
                    // 其他不添加换行符的情况，修改最后一个日志条目的内容
                    var lastLog = logHistory.Last();
                    lastLog.Message += message;
                    lastLog.Color = color; // 更新颜色为最新的颜色
                }
                else
                {
                    // 如果需要添加新的日志条目，先检查前一个条目是否需要添加换行符
                    if (logHistory.Count > 0)
                    {
                        var lastLog = logHistory.Last();
                        lastLog.AddNewLine = true; // 确保前一个条目添加换行符
                    }
                    
                    // 然后添加新的日志条目
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    logHistory.Add(new LogEntry
                    {
                        Timestamp = timestamp,
                        Message = message,
                        Color = color,
                        StartPosition = -1, // 稍后计算
                        Length = -1,
                        AddNewLine = addNewLine
                    });
                }
                
                // 确保最后一个日志条目在操作完成后添加换行符
                // 当一个操作完成时（通常是添加了"ok"或"Error"），我们需要确保下一个操作在新行开始
                if (logHistory.Count > 0)
                {
                    bool isOperationComplete = false;
                    
                    // 检查消息是否是操作完成的标记
                    if (EqualsIgnoreCase(message, "ok") || message == "Error" || message.Contains("Error:") || 
                        ContainsIgnoreCase(message, "...ok") || message.Contains("...Error") || message.Contains("...OK"))
                    {
                        isOperationComplete = true;
                    }
                    
                    // 如果是操作完成标记，确保下一个操作在新行开始
                    if (isOperationComplete)
                    {
                        var lastLog = logHistory.Last();
                        lastLog.AddNewLine = true;
                    }
                }

                // 重建显示
                RebuildAllLogs();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UI日志更新失败: {ex.Message}");
            }
        }

        private LogEntry GetPendingOperationLog()
        {
            if (pendingOperationLogIndex >= 0 && pendingOperationLogIndex < logHistory.Count)
            {
                return logHistory[pendingOperationLogIndex];
            }

            return null;
        }

        /// <summary>
        /// 重建所有日志显示
        /// </summary>
        private void RebuildAllLogs()
        {
            try
            {
                // 暂停UI更新
                this.SuspendLayout();
                input1.SuspendLayout();

                // 清空文本框
                input1.Clear();

                // 如果有ClearStyle方法，调用它
                TryClearStyles();

                // 构建所有文本并记录位置
                StringBuilder allText = new StringBuilder();
                List<Tuple<int, int, Color>> styleInfos = new List<Tuple<int, int, Color>>();

                foreach (var log in logHistory)
                {
                    string formattedLog = $"{log.Timestamp} {log.Message}";

                    // 记录起始位置和长度
                    int start = allText.Length;
                    int length = formattedLog.Length;

                    // 更新历史记录中的位置信息
                    log.StartPosition = start;
                    log.Length = length;

                    // 保存样式信息
                    styleInfos.Add(Tuple.Create(start, length, log.Color));

                    // 添加到文本
                    allText.Append(formattedLog);
                    
                    // 根据AddNewLine属性决定是否添加换行符
                    if (log.AddNewLine)
                    {
                        allText.Append(Environment.NewLine);
                    }
                }

                // 设置文本
                input1.Text = allText.ToString();

                // 应用所有样式
                foreach (var styleInfo in styleInfos)
                {
                    try
                    {
                        int start = styleInfo.Item1;
                        int length = styleInfo.Item2;
                        Color color = styleInfo.Item3;

                        if (start >= 0 && length > 0 && start + length <= input1.Text.Length)
                        {
                            input1.SetStyle(start, length, fore: color);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"设置样式失败: 位置{styleInfo.Item1}, 长度{styleInfo.Item2}, 错误: {ex.Message}");
                    }
                }

                // 滚动到底部
                input1.SelectionStart = input1.Text.Length;
                input1.ScrollToCaret();

                // 恢复UI更新
                input1.ResumeLayout();
                this.ResumeLayout(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重建日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 尝试清空样式
        /// </summary>
        private void TryClearStyles()
        {
            try
            {
                // 尝试使用反射调用ClearStyle方法
                var method = input1.GetType().GetMethod("ClearStyle",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (method != null)
                {
                    method.Invoke(input1, new object[] { false }); // 不重绘
                }
            }
            catch
            {
                // 忽略错误
            }
        }

        // 只有包含特定关键词（更新、封禁、服务器、维护）的消息才强制弹窗
        private bool IsCriticalMessage(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            return msg.Contains("更新") || msg.Contains("Update") ||
                   msg.Contains("封禁") || msg.Contains("Banned") ||
                   msg.Contains("服务器") || msg.Contains("Server") ||
                   msg.Contains("维护");
        }

        // 使用 Form3 弹窗展示关键信息，确保 input3 显示完整内容
        private void ShowCriticalFormAlert(string message)
        {
            using (var criticalForm = new Form3())
            {
                criticalForm.Input3Text = message ?? string.Empty;
                criticalForm.ShowDialog(this);
            }
        }

        // 优化后的信息提示：默认记录日志，关键信息才弹窗
        private void ShowInfoMessage(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowInfoMessage), message);
                return;
            }

            if (IsCriticalMessage(message))
            {
                ShowCriticalFormAlert(message ?? string.Empty);
            }
            else
            {
                LogInfo($"[提示] {message}"); // 蓝色
            }
        }

        // 优化后的警告提示：默认记录日志，关键信息才弹窗
        private void ShowWarnMessage(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowWarnMessage), message);
                return;
            }

            if (IsCriticalMessage(message))
            {
                ShowCriticalFormAlert(message ?? string.Empty);
            }
            else
            {
                LogWarning($"[警告] {message}"); // 黄色(橙色)
            }
        }

        // 优化后的错误提示：默认记录日志，关键信息才弹窗
        private void ShowErrorMessage(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowErrorMessage), message);
                return;
            }

            if (IsCriticalMessage(message))
            {
                ShowCriticalFormAlert(message ?? string.Empty);
            }
            else
            {
                LogError($"[错误] {message}"); // 红色
            }
        }

        /// <summary>
        /// 简单方法：直接在现有文本后追加并设置样式
        /// </summary>
        public void AppendLogSimple(string message, Color color)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<string, Color>(AppendLogSimple), message, color);
                return;
            }

            try
            {
                // 检查并切换日志文件（如果需要）
                CheckAndSwitchLogFile();

                string cleanMessage = message.Replace("\r", "").Replace("\n", " ");
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string formattedLog = $"{timestamp} {cleanMessage}";

                // 写入文件
                WriteLogToFile(message, true);

                // 获取当前文本
                string currentText = input1.Text;

                // 计算起始位置
                int startPosition;
                if (string.IsNullOrEmpty(currentText))
                {
                    startPosition = 0;
                    currentText = formattedLog;
                }
                else
                {
                    if (!currentText.EndsWith(Environment.NewLine))
                    {
                        currentText += Environment.NewLine;
                    }
                    startPosition = currentText.Length;
                    currentText += formattedLog;
                }

                // 设置文本
                input1.Text = currentText;

                // 立即设置样式（不要等待）
                try
                {
                    // 只设置新添加的部分
                    if (startPosition >= 0 && formattedLog.Length > 0 &&
                        startPosition + formattedLog.Length <= input1.Text.Length)
                    {
                        input1.SetStyle(startPosition, formattedLog.Length, fore: color);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"立即设置样式失败: {ex.Message}");

                    // 如果失败，添加到历史记录并重建
                    logHistory.Add(new LogEntry
                    {
                        Timestamp = timestamp,
                        Message = cleanMessage,
                        Color = color,
                        StartPosition = startPosition,
                        Length = formattedLog.Length
                    });

                    // 延迟重建
                    Task.Delay(100).ContinueWith(_ =>
                    {
                        this.Invoke(new Action(RebuildAllLogs));
                    });
                }

                // 滚动到底部
                input1.SelectionStart = input1.Text.Length;
                input1.ScrollToCaret();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"简单追加日志失败: {ex.Message}");
            }
        }

        // ========== 设备状态检测核心 ==========
        private void InitializeDetectionTimer()
        {
            detectionTimer = new System.Windows.Forms.Timer { Interval = 800 };
            detectionTimer.Tick += DetectDeviceStatus;
            detectionTimer.Start();
            UpdateStatusLabel("设备状态: 检测中...");
        }

        private void DetectDeviceStatus(object sender, EventArgs e)
        {
            if (isDeviceRebooting || detectionCancellationRequested) return;

            if (Interlocked.CompareExchange(ref detectionWorkInProgress, 1, 0) == 1)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (detectionCancellationRequested) return;

                    var detectionResult = deviceManager.DetectDeviceStatus();
                    if (detectionCancellationRequested) return;

                    foreach (var logEvent in detectionResult.LogEvents)
                    {
                        AppendLog(logEvent.Message, logEvent.Color);
                    }

                    UpdateDeviceDropdown(detectionResult.Devices, detectionResult.DisplayInfo);
                }
                catch (Exception ex)
                {
                    AppendLog($"设备检测异常: {ex.Message}", Color.Red);
                    UpdateStatusLabel(FormatDeviceStatusText("检测异常"));
                }
                finally
                {
                    Interlocked.Exchange(ref detectionWorkInProgress, 0);
                }
            });
        }

        private string FormatDeviceStatusText(string text)
        {
            string trimmed = string.IsNullOrWhiteSpace(text) ? "未知" : text.Trim();
            const string legacyPrefixFullWidth = "设备状态：";
            const string legacyPrefixAscii = "设备状态:";

            if (trimmed.StartsWith(DeviceStatusPrefix, StringComparison.Ordinal))
            {
                return trimmed;
            }

            if (trimmed.StartsWith(legacyPrefixFullWidth, StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(legacyPrefixFullWidth.Length).TrimStart();
            }
            else if (trimmed.StartsWith(legacyPrefixAscii, StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(legacyPrefixAscii.Length).TrimStart();
            }

            return $"{DeviceStatusPrefix}{trimmed}";
        }

        private string StripDeviceStatusPrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            const string legacyPrefixFullWidth = "设备状态：";
            const string legacyPrefixAscii = "设备状态:";

            if (text.StartsWith(DeviceStatusPrefix, StringComparison.Ordinal))
            {
                return text.Substring(DeviceStatusPrefix.Length).TrimStart();
            }

            if (text.StartsWith(legacyPrefixFullWidth, StringComparison.Ordinal))
            {
                return text.Substring(legacyPrefixFullWidth.Length).TrimStart();
            }

            if (text.StartsWith(legacyPrefixAscii, StringComparison.Ordinal))
            {
                return text.Substring(legacyPrefixAscii.Length).TrimStart();
            }

            return text;
        }

        private string BuildDeviceStatusDisplay(DeviceManager.DeviceInfo device)
        {
            if (device == null)
            {
                return FormatDeviceStatusText("未知模式 | 未知");
            }

            string identifier = !string.IsNullOrWhiteSpace(device.Serial)
                ? device.Serial
                : (!string.IsNullOrWhiteSpace(device.Port) ? device.Port : "未知");

            string modeLabel = GetDeviceModeLabel(device);
            return FormatDeviceStatusText($"{modeLabel} | {identifier}");
        }

        private string GetDeviceModeLabel(DeviceManager.DeviceInfo device)
        {
            if (device == null) return "未知模式";

            string label = device.Mode;

            switch (device.DeviceType)
            {
                case "ADB":
                    label = string.IsNullOrWhiteSpace(device.Mode) ? "系统" : device.Mode;
                    break;
                case "Fastboot":
                    label = string.IsNullOrWhiteSpace(device.Mode) ? "Fastboot" : device.Mode;
                    break;
                case "EDL":
                    label = "9008";
                    break;
                case "Unauthorized":
                    return "未授权设备";
                case "901D":
                    label = "901D";
                    break;
                default:
                    label = string.IsNullOrWhiteSpace(device.Mode) ? (device.DeviceType ?? "未知") : device.Mode;
                    break;
            }

            if (!string.IsNullOrWhiteSpace(label) &&
                !label.EndsWith("模式", StringComparison.Ordinal) &&
                !label.Contains("设备"))
            {
                label += "模式";
            }

            return label;
        }

        /// <summary>
        /// 更新设备下拉菜单（修复的核心方法）
        /// 第一个设备显示在select3.Text，其他设备放在下拉菜单中
        /// </summary>
        private void UpdateDeviceDropdown(List<DeviceManager.DeviceInfo> devices, string statusText)
        {
            var combo = select3;
            if (combo == null)
            {
                return;
            }

            if (combo.InvokeRequired)
            {
                combo.Invoke(new Action<List<DeviceManager.DeviceInfo>, string>(UpdateDeviceDropdown), devices, statusText);
                return;
            }

            try
            {
                combo.SelectedIndexChanged -= select3_SelectedIndexChanged;

                string previousSelection = combo.SelectedValue as string ?? combo.Text;

                var items = combo.Items;
                if (items == null)
                {
                    combo.SelectedIndexChanged += select3_SelectedIndexChanged;
                    combo.Text = FormatDeviceStatusText(statusText);
                    return;
                }

                items.Clear();

                string formattedStatusText = FormatDeviceStatusText(statusText);

                if (devices.Count == 0)
                {
                    combo.Text = formattedStatusText;
                    combo.SelectedIndexChanged += select3_SelectedIndexChanged;
                    return;
                }

                foreach (var device in devices)
                {
                    items.Add(BuildDeviceStatusDisplay(device));
                }

                if (!string.IsNullOrEmpty(previousSelection) && items.Contains(previousSelection))
                {
                    combo.SelectedValue = previousSelection;
                }
                else if (devices.Count == 1 && items.Count > 0)
                {
                    combo.SelectedValue = items[0];
                }
                else if (devices.Count > 1)
                {
                    combo.SelectedIndex = -1;
                }

                if (combo.SelectedValue != null)
                {
                    combo.Text = combo.SelectedValue.ToString();
                }
                else if (devices.Count == 1 && items.Count > 0)
                {
                    var firstItem = items[0];
                    combo.Text = firstItem != null ? firstItem.ToString() : formattedStatusText;
                }
                else
                {
                    combo.Text = formattedStatusText;
                }

                combo.SelectedIndexChanged += select3_SelectedIndexChanged;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新下拉菜单失败: {ex.Message}");
                combo.Text = FormatDeviceStatusText("更新失败");
                combo.SelectedIndexChanged += select3_SelectedIndexChanged;
            }
        }

        /// </summary>
        private void ClearDropdownOptions()
        {
            if (select3.InvokeRequired)
            {
                select3.Invoke(new Action(ClearDropdownOptions));
                return;
            }

            try
            {
                // 清空选项
                if (select3.Items != null)
                {
                    select3.Items.Clear();
                }
                select3.Text = FormatDeviceStatusText("未连接");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空下拉菜单失败: {ex.Message}");
            }
        }
        private void UpdateStatusLabel(string text)
        {
            if (select3.InvokeRequired)
            {
                select3.Invoke((MethodInvoker)(() => select3.Text = FormatDeviceStatusText(text)));
            }
            else
            {
                select3.Text = FormatDeviceStatusText(text);
            }
        }

        private async void Form1_Load(object sender, EventArgs e)
        {
            // [新增] 启动时自动解压依赖到 C:\edltool，使用统一黑色日志
            await Services.DependencyManager.ExtractDependenciesAsync((msg) => LogNormal(msg));

            // 程序启动时自动应用标准模式QC的布局
            // 直接保存原始布局信息，确保布局调整正确执行
            originalInput5Location = input5.Location;
       //     originalInput6Location = input6.Location;
        //    originalButton1Location = button1.Location;
            originalButton2Location = button2.Location;
            originalCheckbox2Location = checkbox2.Location;
            originalListView1Size = listView1.Size;
            originalListView1Location = listView1.Location;
            originalInput7Size = input7.Size;
            originalInput7Location = input7.Location;
            isOriginalLayoutSaved = true;

            // 启用复选框
            listView1.CheckBoxes = true;

            // 应用标准模式QC的布局
            tabPage1.Controls.Remove(input3);
            tabPage1.Controls.Remove(input4);
            
            // 上移input5，input6，button1，button2，checkbox2
            input5.Location = new Point(input5.Location.X, input5.Location.Y - 32);
       //     input6.Location = new Point(input6.Location.X, input6.Location.Y - 32);
       //     button1.Location = new Point(button1.Location.X, button1.Location.Y - 32);
            button2.Location = new Point(button2.Location.X, button2.Location.Y - 32);
            checkbox2.Location = new Point(checkbox2.Location.X, checkbox2.Location.Y - 32);
            
            // 调整listView1和input7的大小和位置
            listView1.Location = new Point(listView1.Location.X, listView1.Location.Y - 32);
            listView1.Size = new Size(listView1.Size.Width, listView1.Size.Height + 32);
            input7.Location = new Point(input7.Location.X, input7.Location.Y - 32);
            input7.Size = new Size(input7.Size.Width, input7.Size.Height + 32);
        }


        private void 清除日志ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            lock (logFilePath)
            {
                try
                {
                    input8.Text = "状态：清理日志...";
                    // 获取日志文件夹路径
                    string logFolderPath = Path.GetDirectoryName(logFilePath);

                    // 删除文件夹内所有文件
                    if (Directory.Exists(logFolderPath))
                    {
                        foreach (string file in Directory.GetFiles(logFolderPath))
                        {
                            File.Delete(file);
                        }
                    }

                    if (!Directory.Exists(logFolderPath) && !string.IsNullOrEmpty(logFolderPath))
                    {
                        Directory.CreateDirectory(logFolderPath);
                    }

                    using (var stream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        writer.WriteLine($"=== Tool日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    }

                    sessionLogFileInitialized = true;
                    ShowInfoMessage("已清空日志");
                    input8.Text = "状态：等待操作...";
                }
                catch (Exception ex)
                {
                    ShowWarnMessage($"清空日志...失败：{ex.Message}");
                    input8.Text = "状态：等待操作...";
                }
            }
            ClearLogDisplay();
        }

        private void 查看日志ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // 获取日志文件夹路径（基于logFilePath的目录）
            string logFolderPath = Path.GetDirectoryName(logFilePath);

            // 确保文件夹存在
            if (Directory.Exists(logFolderPath))
            {
                // 使用资源管理器打开日志文件夹
                Process.Start("explorer.exe", logFolderPath);
                AppendLog("打开日志文件夹...ok", Color.Green);
            }
            else
            {
                AppendLog("日志文件夹不存在", Color.Orange);
            }
        }

        #region 设备重启事件
        
        private async void 重启系统ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("重启设备", out string serial)) return;
            await ExecuteDeviceActionAsync("重启设备...", () => deviceManager.RebootDevice(serial));
        }

        private async void 恢复模式ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("重启到Recovery", out string serial)) return;
            await ExecuteDeviceActionAsync("重启到Recovery...", () => deviceManager.RebootToRecovery(serial));
        }

        private async void fastBootToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("重启到Fastboot", out string serial)) return;
            await ExecuteDeviceActionAsync("重启到Fastboot...", () => deviceManager.RebootToFastboot(serial));
        }

        private async void fastBootDToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("重启到FastbootD", out string serial)) return;
            await ExecuteDeviceActionAsync("重启到FastbootD...", () => deviceManager.RebootToFastbootD(serial));
        }
        
        #endregion

        #region 文件选择事件

        private void button2_Click_1(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "固件文件|rawprogram*.xml;*.ofp;*.ozip;*.ops|rawprogram XML|rawprogram*.xml|OFP 加密固件|*.ofp|OZIP 加密固件|*.ozip|OPS 固件 (OnePlus)|*.ops|XML 文件|*.xml|所有文件|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = "选择固件 (rawprogram XML / OFP / OZIP / OPS)";
                openFileDialog.Multiselect = true;
                openFileDialog.RestoreDirectory = true;
                if (!string.IsNullOrEmpty(currentFirmwareFolder) && Directory.Exists(currentFirmwareFolder))
                {
                    openFileDialog.InitialDirectory = currentFirmwareFolder;
                }

                if (openFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                string selectedFile = openFileDialog.FileName;
                if (string.IsNullOrEmpty(selectedFile) || !File.Exists(selectedFile))
                {
                    ShowWarnMessage("请选择有效的固件文件");
                    return;
                }

                // 检测固件类型并处理加密固件包 (OFP/OZIP/OPS)
                var firmwareType = Qualcomm.OFPDecryptor.DetectFirmwareType(selectedFile);
                if (firmwareType != Qualcomm.OFPDecryptor.FirmwareType.Unknown)
                {
                    string typeStr = firmwareType switch
                    {
                        Qualcomm.OFPDecryptor.FirmwareType.OFP => "OFP",
                        Qualcomm.OFPDecryptor.FirmwareType.OZIP => "OZIP",
                        Qualcomm.OFPDecryptor.FirmwareType.OPS => "OPS",
                        _ => "加密"
                    };
                    // 异步处理加密固件解密
                    SafeExecuteAsync(async () => await LoadEncryptedFirmwareAsync(selectedFile, firmwareType), $"加载 {typeStr} 固件");
                    return;
                }
                
                // 如果扩展名是 .ofp/.ozip/.ops 但未检测到，也尝试解密
                string ext = Path.GetExtension(selectedFile).ToLower();
                if (ext == ".ofp" || ext == ".ozip" || ext == ".ops")
                {
                    SafeExecuteAsync(async () => await LoadEncryptedFirmwareAsync(selectedFile, Qualcomm.OFPDecryptor.FirmwareType.Unknown), "加载加密固件");
                    return;
                }

                string selectedRawprogram = selectedFile;
                currentFirmwareFolder = Path.GetDirectoryName(selectedRawprogram) ?? string.Empty;

                var allRawFiles = Directory.GetFiles(currentFirmwareFolder, "rawprogram*.xml", SearchOption.TopDirectoryOnly);
                var rawFiles = allRawFiles.Where(f =>
                {
                    string name = Path.GetFileName(f);
                    return System.Text.RegularExpressions.Regex.IsMatch(name, @"^rawprogram\d*\.xml$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

                if (!rawFiles.Any())
                {
                    rawFiles.Add(selectedRawprogram);
                }
                else if (!rawFiles.Any(f => string.Equals(f, selectedRawprogram, StringComparison.OrdinalIgnoreCase)))
                {
                    rawFiles.Insert(0, selectedRawprogram);
                }

                // 默认显示 rawprogram0.xml 或第一个文件
                string mainRawXml = rawFiles.FirstOrDefault(f => Path.GetFileName(f).Equals("rawprogram0.xml", StringComparison.OrdinalIgnoreCase)) ?? rawFiles[0];
                input5.Text = mainRawXml;

                currentPatchFiles.Clear();
                var patchFiles = Directory.GetFiles(currentFirmwareFolder, "patch*.xml", SearchOption.TopDirectoryOnly);
                if (patchFiles.Length > 0)
                {
                    var orderedPatches = patchFiles
                        .Select(path => new
                        {
                            Path = path,
                            Index = GetPatchIndex(Path.GetFileName(path))
                        })
                        .OrderBy(p => p.Index.HasValue ? 0 : 1)
                        .ThenBy(p => p.Index ?? int.MaxValue)
                        .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var patch in orderedPatches)
                    {
                        currentPatchFiles.Add(patch.Path);
                    }

                    var names = currentPatchFiles.Select(Path.GetFileName);
                    AppendLog($"已匹配 Patch XML: {string.Join(", ", names)}", Color.Blue);
                }
                else
                {
                    AppendLog("未在目录中找到 patch*.xml 文件，保持当前值", Color.Orange);
                }

                try
                {
                    var allPartitions = new List<PartitionInfo>();

                    foreach (var rawFile in rawFiles)
                    {
                        var xmlPartitions = XmlPartitionParser.Parse(rawFile);
                        foreach (var xp in xmlPartitions)
                        {
                            var p = new PartitionInfo();
                            int.TryParse(xp.Lun, out int lun);
                            p.Lun = lun;
                            p.Name = xp.Label;
                            p.StartLbaStr = xp.StartSector;
                            ulong.TryParse(xp.StartSector, out ulong startLba);
                            p.StartLba = startLba;
                            p.Sectors = (ulong)xp.NumSectors;
                            p.SectorSize = xp.SectorSize;
                            p.FileName = xp.FileName;
                            p.Source = PartitionSource.XmlFile;
                            p.SourceFile = rawFile;
                            allPartitions.Add(p);
                        }
                    }

                    // 检测镜像文件的文件系统类型和格式
                    string imagesDir = Path.GetDirectoryName(rawFiles.First());
                    int detectedCount = 0;
                    foreach (var part in allPartitions)
                    {
                        if (!string.IsNullOrEmpty(part.FileName))
                        {
                            string imagePath = Path.Combine(imagesDir, part.FileName);
                            if (File.Exists(imagePath))
                            {
                                DetectPartitionImageFormat(part, imagePath);
                                detectedCount++;
                            }
                        }
                    }
                    
                    if (detectedCount > 0)
                    {
                        AppendLog($"已检测 {detectedCount} 个镜像文件的格式", Color.Blue);
                    }

                    UpdatePartitionList(allPartitions);
                    hasXmlPartitions = true; // 标记已从 XML 加载分区数据
                    LogStatus($"已从 {rawFiles.Count} 个 rawprogram XML 解析分区表", true);
                    
                    // 输出分区风险统计
                    LogPartitionSummary(allPartitions);
                }
                catch (Exception ex)
                {
                    ShowErrorMessage($"解析 XML 失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 加载 OFP 加密固件包
        /// </summary>
        private async Task LoadOFPFirmwareAsync(string ofpPath)
        {
            LogSection("加载 OFP 加密固件");
            LogKeyValue("文件", Path.GetFileName(ofpPath));
            
            // 创建临时解密目录
            string tempDir = Path.Combine(Path.GetTempPath(), $"ofp_extract_{Guid.NewGuid():N}");
            
            try
            {
                // 创建解密器
                var decryptor = new Qualcomm.OFPDecryptor(
                    (msg) => AppendLog(msg, Color.Gray),
                    (current, total) => { }
                );
                
                _cts = new CancellationTokenSource();
                
                // 先快速提取 XML 文件
                LogWaiting("解密固件包");
                var result = await decryptor.ExtractXmlOnlyAsync(ofpPath, tempDir, _cts.Token);
                
                if (!result.Success)
                {
                    LogStatus("解密 OFP", false, result.Error);
                    return;
                }
                
                LogStatus("解密 OFP", true);
                
                // 检查是否找到 rawprogram XML
                if (result.RawProgramXmlPaths.Count == 0)
                {
                    LogStatus("查找 rawprogram XML", false, "未找到分区配置文件");
                    return;
                }
                
                LogStatus($"找到 {result.RawProgramXmlPaths.Count} 个 rawprogram XML", true);
                
                // 设置固件目录
                currentFirmwareFolder = tempDir;
                
                // 加载 patch 文件
                currentPatchFiles.Clear();
                foreach (var patchPath in result.PatchXmlPaths.OrderBy(p => GetPatchIndex(Path.GetFileName(p)) ?? int.MaxValue))
                {
                    currentPatchFiles.Add(patchPath);
                }
                
                if (currentPatchFiles.Count > 0)
                {
                    var names = currentPatchFiles.Select(Path.GetFileName);
                    LogKeyValue("Patch XML", string.Join(", ", names));
                }
                
                // 显示主 XML 路径
                string mainRawXml = result.RawProgramXmlPaths
                    .FirstOrDefault(f => Path.GetFileName(f).Equals("rawprogram0.xml", StringComparison.OrdinalIgnoreCase))
                    ?? result.RawProgramXmlPaths[0];
                input5.Text = mainRawXml;
                
                // 解析分区表
                var allPartitions = new List<PartitionInfo>();
                
                foreach (var rawFile in result.RawProgramXmlPaths.OrderBy(f => f))
                {
                    var xmlPartitions = XmlPartitionParser.Parse(rawFile);
                    foreach (var xp in xmlPartitions)
                    {
                        var p = new PartitionInfo();
                        int.TryParse(xp.Lun, out int lun);
                        p.Lun = lun;
                        p.Name = xp.Label;
                        p.StartLbaStr = xp.StartSector;
                        ulong.TryParse(xp.StartSector, out ulong startLba);
                        p.StartLba = startLba;
                        p.Sectors = (ulong)xp.NumSectors;
                        p.SectorSize = xp.SectorSize;
                        p.FileName = xp.FileName;
                        p.Source = PartitionSource.XmlFile;
                        p.SourceFile = rawFile;
                        allPartitions.Add(p);
                    }
                }
                
                // 询问用户是否解密全部镜像文件
                var dialogResult = MessageBox.Show(
                    $"已解析 {allPartitions.Count} 个分区配置\n\n" +
                    "是否解密全部镜像文件？\n" +
                    "• 点击 [是] - 解密全部镜像 (可能需要较长时间)\n" +
                    "• 点击 [否] - 仅使用 XML 配置 (稍后按需解密)\n" +
                    "• 点击 [取消] - 取消操作",
                    "OFP 固件解密",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                
                if (dialogResult == DialogResult.Cancel)
                {
                    LogWarning("用户取消操作");
                    return;
                }
                
                if (dialogResult == DialogResult.Yes)
                {
                    // 解密全部镜像
                    LogSection("解密全部镜像文件");
                    var fullResult = await decryptor.ExtractAsync(ofpPath, tempDir, _cts.Token);
                    
                    if (fullResult.Success)
                    {
                        LogStatus($"解密完成，共 {fullResult.ExtractedFiles.Count} 个文件", true);
                        
                        // 检测镜像格式
                        int detectedCount = 0;
                        foreach (var part in allPartitions)
                        {
                            if (!string.IsNullOrEmpty(part.FileName))
                            {
                                string imagePath = Path.Combine(tempDir, part.FileName);
                                if (File.Exists(imagePath))
                                {
                                    DetectPartitionImageFormat(part, imagePath);
                                    detectedCount++;
                                }
                            }
                        }
                        
                        if (detectedCount > 0)
                        {
                            LogKeyValue("检测镜像格式", $"{detectedCount} 个");
                        }
                    }
                    else
                    {
                        LogWarning($"部分解密失败: {fullResult.Error}");
                    }
                }
                
                // 更新分区列表
                UpdatePartitionList(allPartitions);
                hasXmlPartitions = true; // 标记已从 OFP/XML 加载分区数据
                LogStatus($"已加载 OFP 固件，共 {allPartitions.Count} 个分区", true);
                
                // 输出分区风险统计
                LogPartitionSummary(allPartitions);
                
                // 记录临时目录供清理
                _ofpTempDir = tempDir;
            }
            catch (OperationCanceledException)
            {
                LogWarning("操作已取消");
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                LogStatus("加载 OFP", false, ex.Message);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // OFP 临时解压目录
        private string _ofpTempDir;

        /// <summary>
        /// 加载加密固件包 (支持 OFP/OZIP/OPS)
        /// </summary>
        private async Task LoadEncryptedFirmwareAsync(string firmwarePath, Qualcomm.OFPDecryptor.FirmwareType detectedType)
        {
            string typeStr = detectedType switch
            {
                Qualcomm.OFPDecryptor.FirmwareType.OFP => "OFP",
                Qualcomm.OFPDecryptor.FirmwareType.OZIP => "OZIP",
                Qualcomm.OFPDecryptor.FirmwareType.OPS => "OPS",
                _ => "加密"
            };
            
            LogSection($"加载 {typeStr} 固件包");
            LogKeyValue("文件", Path.GetFileName(firmwarePath));
            
            // 创建临时解密目录
            string tempDir = Path.Combine(Path.GetTempPath(), $"firmware_extract_{Guid.NewGuid():N}");
            
            try
            {
                // 创建解密器
                var decryptor = new Qualcomm.OFPDecryptor(
                    (msg) => AppendLog(msg, Color.Gray),
                    (current, total) => { }
                );
                
                _cts = new CancellationTokenSource();
                
                // 使用智能解密方法
                LogWaiting("解密固件包");
                Qualcomm.OFPExtractResult result;
                
                // 根据类型选择解密方法
                if (detectedType == Qualcomm.OFPDecryptor.FirmwareType.OFP)
                {
                    // OFP: 先快速提取 XML
                    result = await decryptor.ExtractXmlOnlyAsync(firmwarePath, tempDir, _cts.Token);
                }
                else
                {
                    // OZIP/OPS: 使用智能解密 (会解压全部文件)
                    result = await decryptor.SmartExtractAsync(firmwarePath, tempDir, _cts.Token);
                }
                
                if (!result.Success)
                {
                    LogStatus($"解密 {typeStr}", false, result.Error);
                    return;
                }
                
                LogStatus($"解密 {typeStr}", true);
                
                // 检查是否找到 rawprogram XML
                if (result.RawProgramXmlPaths.Count == 0)
                {
                    // 尝试搜索解压目录中的 XML 文件
                    var rawFiles = Directory.GetFiles(tempDir, "rawprogram*.xml", SearchOption.AllDirectories);
                    result.RawProgramXmlPaths = rawFiles.ToList();
                    
                    var patchFiles = Directory.GetFiles(tempDir, "patch*.xml", SearchOption.AllDirectories);
                    result.PatchXmlPaths = patchFiles.ToList();
                }
                
                if (result.RawProgramXmlPaths.Count == 0)
                {
                    LogStatus("查找 rawprogram XML", false, "未找到分区配置文件");
                    
                    // 显示提取的文件列表供用户参考
                    if (result.ExtractedFiles.Count > 0)
                    {
                        LogKeyValue("已提取文件", $"{result.ExtractedFiles.Count} 个");
                        foreach (var f in result.ExtractedFiles.Take(10))
                        {
                            AppendLog($"  • {Path.GetFileName(f)}", Color.Gray);
                        }
                        if (result.ExtractedFiles.Count > 10)
                        {
                            AppendLog($"  ... 还有 {result.ExtractedFiles.Count - 10} 个文件", Color.Gray);
                        }
                    }
                    return;
                }
                
                LogStatus($"找到 {result.RawProgramXmlPaths.Count} 个 rawprogram XML", true);
                
                // 设置固件目录
                currentFirmwareFolder = tempDir;
                
                // 加载 patch 文件
                currentPatchFiles.Clear();
                foreach (var patchPath in result.PatchXmlPaths.OrderBy(p => GetPatchIndex(Path.GetFileName(p)) ?? int.MaxValue))
                {
                    currentPatchFiles.Add(patchPath);
                }
                
                if (currentPatchFiles.Count > 0)
                {
                    var names = currentPatchFiles.Select(Path.GetFileName);
                    LogKeyValue("Patch XML", string.Join(", ", names));
                }
                
                // 显示主 XML 路径
                string mainRawXml = result.RawProgramXmlPaths
                    .FirstOrDefault(f => Path.GetFileName(f).Equals("rawprogram0.xml", StringComparison.OrdinalIgnoreCase))
                    ?? result.RawProgramXmlPaths[0];
                input5.Text = mainRawXml;
                
                // 解析分区表
                var allPartitions = new List<PartitionInfo>();
                
                foreach (var rawFile in result.RawProgramXmlPaths.OrderBy(f => f))
                {
                    var xmlPartitions = XmlPartitionParser.Parse(rawFile);
                    foreach (var xp in xmlPartitions)
                    {
                        var p = new PartitionInfo();
                        int.TryParse(xp.Lun, out int lun);
                        p.Lun = lun;
                        p.Name = xp.Label;
                        p.StartLbaStr = xp.StartSector;
                        ulong.TryParse(xp.StartSector, out ulong startLba);
                        p.StartLba = startLba;
                        p.Sectors = (ulong)xp.NumSectors;
                        p.SectorSize = xp.SectorSize;
                        p.FileName = xp.FileName;
                        p.Source = PartitionSource.XmlFile;
                        p.SourceFile = rawFile;
                        allPartitions.Add(p);
                    }
                }
                
                // 检测镜像格式 (如果是 OZIP/OPS 已经解压了)
                if (detectedType != Qualcomm.OFPDecryptor.FirmwareType.OFP)
                {
                    int detectedCount = 0;
                    foreach (var part in allPartitions)
                    {
                        if (!string.IsNullOrEmpty(part.FileName))
                        {
                            string imagePath = Path.Combine(tempDir, part.FileName);
                            if (File.Exists(imagePath))
                            {
                                DetectPartitionImageFormat(part, imagePath);
                                detectedCount++;
                            }
                        }
                    }
                    
                    if (detectedCount > 0)
                    {
                        LogKeyValue("检测镜像格式", $"{detectedCount} 个");
                    }
                }
                else
                {
                    // OFP: 询问用户是否解密全部镜像文件
                    var dialogResult = MessageBox.Show(
                        $"已解析 {allPartitions.Count} 个分区配置\n\n" +
                        "是否解密全部镜像文件？\n" +
                        "• 点击 [是] - 解密全部镜像 (可能需要较长时间)\n" +
                        "• 点击 [否] - 仅使用 XML 配置 (稍后按需解密)\n" +
                        "• 点击 [取消] - 取消操作",
                        "OFP 固件解密",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);
                    
                    if (dialogResult == DialogResult.Cancel)
                    {
                        LogWarning("用户取消操作");
                        return;
                    }
                    
                    if (dialogResult == DialogResult.Yes)
                    {
                        // 解密全部镜像
                        LogSection("解密全部镜像文件");
                        var fullResult = await decryptor.ExtractAsync(firmwarePath, tempDir, _cts.Token);
                        
                        if (fullResult.Success)
                        {
                            LogStatus($"解密完成，共 {fullResult.ExtractedFiles.Count} 个文件", true);
                            
                            // 检测镜像格式
                            int detectedCount = 0;
                            foreach (var part in allPartitions)
                            {
                                if (!string.IsNullOrEmpty(part.FileName))
                                {
                                    string imagePath = Path.Combine(tempDir, part.FileName);
                                    if (File.Exists(imagePath))
                                    {
                                        DetectPartitionImageFormat(part, imagePath);
                                        detectedCount++;
                                    }
                                }
                            }
                            
                            if (detectedCount > 0)
                            {
                                LogKeyValue("检测镜像格式", $"{detectedCount} 个");
                            }
                        }
                        else
                        {
                            LogWarning($"部分解密失败: {fullResult.Error}");
                        }
                    }
                }
                
                // 更新分区列表
                UpdatePartitionList(allPartitions);
                hasXmlPartitions = true; // 标记已从 XML 加载分区数据
                LogStatus($"已加载 {typeStr} 固件，共 {allPartitions.Count} 个分区", true);
                
                // 输出分区风险统计
                LogPartitionSummary(allPartitions);
                
                // 记录临时目录供清理
                _ofpTempDir = tempDir;
            }
            catch (OperationCanceledException)
            {
                LogWarning("操作已取消");
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
            catch (Exception ex)
            {
                LogStatus($"加载 {typeStr}", false, ex.Message);
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        private int? GetPatchIndex(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var match = Regex.Match(fileName, @"patch(\d+)\.xml", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int value))
            {
                return value;
            }

            return null;
        }

        private void checkbox2_CheckedChanged(object sender, EventArgs e)
        {
            if (listView1 == null)
            {
                return;
            }

            listView1.BeginUpdate();
            foreach (ListViewItem item in listView1.Items)
            {
                item.Checked = checkbox2.Checked;
            }
            listView1.EndUpdate();
        }

        private void listView1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            var hitInfo = listView1.HitTest(e.Location);
            if (hitInfo?.Item == null)
            {
                return;
            }

            if (hitInfo.Item.Tag is not PartitionInfo part)
            {
                return;
            }

            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "镜像文件|*.img;*.bin;*.mbn;*.elf;*.hex|所有文件|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = $"选择 {part.Name} 对应的文件";
                openFileDialog.RestoreDirectory = true;

                string existingPath = ResolvePartitionFilePath(part.FileName);
                if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
                {
                    openFileDialog.InitialDirectory = Path.GetDirectoryName(existingPath);
                    openFileDialog.FileName = Path.GetFileName(existingPath);
                }
                else if (!string.IsNullOrEmpty(currentFirmwareFolder) && Directory.Exists(currentFirmwareFolder))
                {
                    openFileDialog.InitialDirectory = currentFirmwareFolder;
                }

                if (openFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                part.FileName = GetRelativeFirmwarePath(openFileDialog.FileName);
                ApplyPartitionFileState(hitInfo.Item, part);
                hitInfo.Item.Checked = true;
                AppendLog($"分区 {part.Name} 已选择文件: {Path.GetFileName(openFileDialog.FileName)}", Color.Blue);
            }
        }

        private void listView2_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            var hitInfo = listView2?.HitTest(e.Location);
            if (hitInfo?.Item?.Tag is not FastbootListEntry entry)
            {
                return;
            }

            if (entry.Payload is not FastbootTask task || !string.Equals(task.Type, "flash", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "镜像文件|*.img;*.bin;*.mbn;*.elf;*.hex|所有文件|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = $"选择 {entry.Name} 对应的文件";
                openFileDialog.RestoreDirectory = true;

                if (!string.IsNullOrWhiteSpace(task.Path))
                {
                    string initialDir = Path.GetDirectoryName(task.Path);
                    if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                    {
                        openFileDialog.InitialDirectory = initialDir;
                    }
                    openFileDialog.FileName = Path.GetFileName(task.Path);
                }

                if (openFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                task.Path = openFileDialog.FileName;
                entry.Source = task.Path;
                try
                {
                    entry.Size = FormatSize((ulong)new FileInfo(task.Path).Length);
                }
                catch
                {
                    entry.Size = "-";
                }

                entry.IsChecked = true;
                task.IsChecked = true;

                suppressListView2CheckEvents = true;
                hitInfo.Item.Checked = true;
                if (hitInfo.Item.SubItems.Count > 1)
                {
                    hitInfo.Item.SubItems[1].Text = entry.Size;
                }
                if (hitInfo.Item.SubItems.Count > 2)
                {
                    hitInfo.Item.SubItems[2].Text = entry.Source;
                }
                suppressListView2CheckEvents = false;

                AppendFastbootLog($"已选择 {entry.Name}: {Path.GetFileName(task.Path)}");
                RefreshFastbootOptionStates();
            }
        }

        private async void 小米踢EDLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AppendLog("检查设备当前状态", Color.Orange);
            if (!TryGetSerialForAction("小米踢EDL", out string serial)) return;
            await ExecuteDeviceActionAsync("小米踢EDL...", () => deviceManager.KickXiaomiToEdl(serial));
        }

        private async void 联想或安卓踢EDLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("安卓或联想踢入EDL", out string serial)) return;
            await ExecuteDeviceActionAsync("安卓或联想踢入EDL...", () => deviceManager.KickLenovoOrAndroidToEdl(serial));
        }

        private async void 切换槽位ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("切换槽位", out string serial)) return;

            try
            {
                AppendLog("切换槽位...", Color.Black);
                var result = await deviceManager.SwitchSlot(serial);
                AppendLog($"当前槽位: {result.CurrentSlot}，切换到槽位: {result.TargetSlot}", Color.Blue);
                AppendLog(result.Success ? "ok" : "Error", result.Success ? Color.Green : Color.Red);
            }
            catch (Exception ex)
            {
                AppendLog("Error", Color.Red);
                AppendLog($"切换槽位失败: {ex.Message}", Color.Red);
            }
        }

        private async void fB去除谷歌锁ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("去除谷歌锁", out string serial)) return;
            await ExecuteDeviceActionAsync("去除谷歌锁...", () => deviceManager.EraseFRP(serial));
        }

        /// <summary>
        /// 获取当前选择的设备序列号
        /// </summary>
        /// <returns>设备序列号</returns>
        private string GetSelectedDeviceSerial()
        {
            string selectedText = StripDeviceStatusPrefix(select3.Text);

            if (string.IsNullOrWhiteSpace(selectedText) || selectedText.StartsWith("未授权设备", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int separatorIndex = selectedText.LastIndexOf('|');
            if (separatorIndex >= 0 && separatorIndex < selectedText.Length - 1)
            {
                return selectedText.Substring(separatorIndex + 1).Trim();
            }

            return string.Empty;
        }

        private bool TryGetSerialForAction(string actionName, out string serial)
        {
            serial = GetSelectedDeviceSerial();
            if (string.IsNullOrEmpty(serial))
            {
                AppendLog($"{actionName}...Error", Color.Red);
                return false;
            }

            return true;
        }

        private async Task ExecuteDeviceActionAsync(string startMessage, Func<Task<bool>> action)
        {
            try
            {
                AppendLog(startMessage, Color.Black, false);
                bool success = await action();
                AppendLog(success ? "ok" : "Error", success ? Color.Green : Color.Red, false);
            }
            catch (Exception ex)
            {
                AppendLog("Error", Color.Red, false);
                AppendLog($"操作失败: {ex.Message}", Color.Red);
            }
        }
        
        #endregion

        #region 刷写操作

        private async void button3_Click(object sender, EventArgs e)
        {
            // [优化] 60秒倒计时检测
            int timeout = 60;
            bool found = false;
            
            AppendLog("正在检测设备 (60s)...", Color.Black);
            
            while (timeout > 0)
            {
                // [优化] 在后台线程执行检测，避免阻塞 UI
                var result = await Task.Run(() => deviceManager.DetectDeviceStatus());
                
                // 检查是否有 EDL 设备
                if (result.Devices.Any(d => d.DeviceType == "EDL" || d.Mode == "9008"))
                {
                    found = true;
                    break;
                }

                await Task.Delay(1000);
                timeout--;
                if (timeout % 5 == 0) AppendLog($"等待设备... {timeout}s", Color.Gray);
            }

            if (found)
            {
                AppendLog("检测到设备！刷新端口列表...", Color.Green);
            }
            else
            {
                AppendLog("未检测到设备 (超时)", Color.Red);
            }
        }

        private void button8_Click(object sender, EventArgs e)
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                AppendLog("正在停止操作...", Color.Red);
            }
        }

        private async void 合并SuperToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string? rootDir = SelectDirectoryWithFileDialog("请选择固件根目录 (包含 META 和 IMAGES 文件夹)");
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                AppendLog("已取消选择固件目录", Color.Gray);
                return;
            }

            string rootDirPath = rootDir!;
            string metaDir = Path.Combine(rootDirPath, "META");

            // 1. 查找 JSON 配置文件
            string? jsonPath = null;

            // 优先在 META 目录下查找
            if (Directory.Exists(metaDir))
            {
                var jsonFiles = Directory.GetFiles(metaDir, "*.json");
                // 优先找 super_def*.json，否则取第一个 json
                jsonPath = jsonFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("super_def", StringComparison.OrdinalIgnoreCase))
                           ?? jsonFiles.FirstOrDefault();
            }

            // 如果 META 下没找到，尝试在根目录下查找
            if (jsonPath == null)
            {
                var jsonFiles = Directory.GetFiles(rootDirPath, "*.json");
                jsonPath = jsonFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("super_def", StringComparison.OrdinalIgnoreCase))
                           ?? jsonFiles.FirstOrDefault();
            }

            if (jsonPath == null)
            {
                AppendLog("错误: 未找到 super_def 配置文件 (JSON)", Color.Red);
                ShowErrorMessage("在所选目录及其 META 子目录中未找到 JSON 配置文件！");
                return;
            }

            // 让 SuperMaker 自动选择输出目录（优先 IMAGES，否则根目录）
            string? outputDir = null;

            AppendLog($"[Super] 选中根目录: {rootDirPath}", Color.Black);
            AppendLog($"[Super] 找到配置文件: {Path.GetFileName(jsonPath)}", Color.Blue);

            await Task.Run(async () =>
            {
                var maker = new SuperMaker(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
                // 关键: 传入 rootDir 作为 imageRootDir，这样 SuperMaker 就能正确解析 IMAGES/xxx.img
                bool success = await maker.MakeSuperImgAsync(jsonPath, outputDir!, rootDirPath);
                AppendLog(success ? "Super 生成成功" : "Super 生成失败", success ? Color.Green : Color.Red);
            });
        }

        private async Task RunEdlCommandAsync(string xml, string actionName)
        {
            if (!TryGetSerialForAction(actionName, out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
            {
                AppendLog($"执行: {actionName}...", Color.Black);
                executor.Client.SendXmlCommand(xml);
                AppendLog("指令已发送", Color.Green);
                await Task.Delay(1000); // Give it a moment
            }, _cts.Token);
        }

        private async Task RunEdlEraseAsync(string partitionName)
        {
            if (!TryGetSerialForAction($"擦除 {partitionName}", out string port)) return;
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
            {
                AppendLog($"正在查找分区: {partitionName}...", Color.Black);
                var partitions = await executor.GetPartitionsAsync(_cts.Token);
                var part = partitions.FirstOrDefault(p => p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));
                
                if (part != null)
                {
                    AppendLog($"找到分区 {part.Name} (LUN{part.Lun})，开始擦除...", Color.Blue);
                    await executor.ErasePartitionAsync(part, _cts.Token);
                    AppendLog($"擦除 {partitionName} 成功", Color.Green);
                }
                else
                {
                    // Try "config" if "frp" not found
                    if (partitionName.Equals("frp", StringComparison.OrdinalIgnoreCase))
                    {
                        part = partitions.FirstOrDefault(p => p.Name.Equals("config", StringComparison.OrdinalIgnoreCase));
                        if (part != null)
                        {
                            AppendLog($"未找到 frp，但找到 config (LUN{part.Lun})，开始擦除...", Color.Blue);
                            await executor.ErasePartitionAsync(part, _cts.Token);
                            AppendLog($"擦除 config 成功", Color.Green);
                            return;
                        }
                    }
                    AppendLog($"未找到分区: {partitionName}", Color.Red);
                }
            }, _cts.Token);
        }

        private List<PartitionInfo> GetSelectedOrCheckedPartitions()
        {
            var list = new List<PartitionInfo>();
            if (listView1.SelectedItems.Count > 0)
            {
                foreach (ListViewItem item in listView1.SelectedItems)
                {
                    if (item.Tag is PartitionInfo p) list.Add(p);
                }
            }
            
            // 如果没有高亮选中，或者同时也勾选了复选框，把勾选的也加进去 (去重)
            if (listView1.CheckedItems.Count > 0)
            {
                foreach (ListViewItem item in listView1.CheckedItems)
                {
                    if (item.Tag is PartitionInfo p) list.Add(p);
                }
            }
            
            return list.Distinct().ToList();
        }

        private async void button5_Click(object sender, EventArgs e)
        {
            // 检查是否有分区数据（从设备读取或从 XML 加载）
            if (!isGptRead && !hasXmlPartitions)
            {
                ShowWarnMessage("请先读取分区表或加载固件 XML 后再进行操作");
                return;
            }

            var partitions = GetSelectedOrCheckedPartitions();
            if (partitions.Count == 0)
            {
                ShowWarnMessage("请先选择至少一个分区 (点击行或勾选)");
                return;
            }

            if (!TryGetSerialForAction("读取分区", out string port)) return;
             if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            string? saveDir = SelectDirectoryWithFileDialog($"即将读取 {partitions.Count} 个分区，请选择保存目录");
            if (string.IsNullOrWhiteSpace(saveDir))
            {
                AppendLog("已取消保存目录选择", Color.Gray);
                return;
            }

            string saveDirPath = saveDir!;
            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            bool success = await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
            {
                foreach (var part in partitions)
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    string fileName = $"{part.Name}.img";
                    string savePath = Path.Combine(saveDirPath, fileName);

                    AppendLog($"正在读取分区: {part.Name} (LUN{part.Lun}) -> {fileName}", Color.Blue);
                    await executor.ReadPartitionAsync(part, savePath, _cts.Token);
                }
            }, _cts.Token);

            if (success)
            {
                AppendLog($"成功读取 {partitions.Count} 个分区到 {saveDirPath}", Color.Green);
                if (checkbox3.Checked)
                {
                    await deviceManager.RebootDevice(port);
                }
            }
        }
        private async void button4_Click_1(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("读取分区表", out string serial)) return;
            
            string port = serial; 
            if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            bool success = await flasher.RunFlashActionAsync(
                port, 
                    progPath!,
                authType, 
                checkbox4.Checked, 
                digest,
                signature,
                async (executor) =>
                {
                    LogSection("读取分区表");
                    LogWaiting("读取 GPT");
                    var partitions = await executor.GetPartitionsAsync(_cts.Token);

                    if (!isGptRead)
                    {
                        isGptRead = true;
                        checkbox4.Checked = true;
                    }

                    if (listView1.InvokeRequired)
                    {
                        listView1.Invoke(new Action(() => UpdatePartitionList(partitions)));
                    }
                    else
                    {
                        UpdatePartitionList(partitions);
                    }
                    
                    LogStatus($"读取分区表完成，共 {partitions.Count} 个分区", true);
                    
                    // 输出分区风险统计
                    LogPartitionSummary(partitions);

                    if (checkbox7.Checked)
                    {
                        string xmlPath = Path.Combine(Application.StartupPath, "rawprogram0.xml");
                        XmlPartitionParser.GenerateXml(partitions, xmlPath);
                        LogStatus($"生成分区表 XML", true, xmlPath);
                    }
                    
                    // 自动读取 Android 设备信息 (build.prop)
                    await ReadAndDisplayDeviceInfoAsync(executor.Client, partitions);
                },
                _cts.Token
            );

            if (success && checkbox3.Checked)
            {
                await deviceManager.RebootDevice(port);
            }
        }

        /// <summary>
        /// 读取并显示 Android 设备信息 (build.prop)
        /// </summary>
        private async Task ReadAndDisplayDeviceInfoAsync(FirehoseClient firehose, List<PartitionInfo> partitions)
        {
            if (firehose == null || partitions == null || partitions.Count == 0)
                return;

            try
            {
                LogSection("读取 Android 设备信息");
                LogWaiting("解析 build.prop");
                
                var reader = new Qualcomm.DeviceInfoReader(firehose, (msg) => AppendLog(msg, Color.Gray));
                var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                
                var buildProps = await reader.ReadBuildPropsAsync(partitions, cts.Token);
                
                if (buildProps != null && !string.IsNullOrEmpty(buildProps.Brand))
                {
                    LogSection("Android 系统信息");
                    
                    // 主要信息
                    if (!string.IsNullOrEmpty(buildProps.Brand))
                        LogDeviceInfo("品牌", buildProps.Brand);
                    if (!string.IsNullOrEmpty(buildProps.Model))
                        LogDeviceInfo("型号", buildProps.Model);
                    if (!string.IsNullOrEmpty(buildProps.Device))
                        LogDeviceInfo("设备代号", buildProps.Device);
                    if (!string.IsNullOrEmpty(buildProps.Product))
                        LogDeviceInfo("产品", buildProps.Product);
                    if (!string.IsNullOrEmpty(buildProps.Manufacturer))
                        LogDeviceInfo("制造商", buildProps.Manufacturer);
                    
                    // Android 版本信息
                    if (!string.IsNullOrEmpty(buildProps.AndroidVersion))
                        LogDeviceInfo("Android", buildProps.AndroidVersion + (string.IsNullOrEmpty(buildProps.SdkVersion) ? "" : $" (SDK {buildProps.SdkVersion})"));
                    if (!string.IsNullOrEmpty(buildProps.SecurityPatch))
                        LogDeviceInfo("安全补丁", buildProps.SecurityPatch);
                    
                    // 版本信息
                    if (!string.IsNullOrEmpty(buildProps.BuildId))
                        LogDeviceInfo("Build ID", buildProps.BuildId);
                    if (!string.IsNullOrEmpty(buildProps.RomVersion))
                        LogDeviceInfo("ROM 版本", buildProps.RomVersion);
                    if (!string.IsNullOrEmpty(buildProps.Incremental))
                        LogDeviceInfo("版本号", buildProps.Incremental);
                    
                    // Fingerprint (可能很长，截断显示)
                    if (!string.IsNullOrEmpty(buildProps.BuildFingerprint))
                    {
                        string fp = buildProps.BuildFingerprint;
                        if (fp.Length > 60)
                            fp = fp.Substring(0, 57) + "...";
                        LogDeviceInfo("Fingerprint", fp);
                    }
                    
                    LogStatus("读取设备信息", true);
                }
                else
                {
                    LogStatus("读取设备信息", false, "无法解析 build.prop");
                    LogWarning("提示: 设备可能使用加密或不支持的文件系统 (EROFS)");
                }
            }
            catch (OperationCanceledException)
            {
                LogStatus("读取设备信息", false, "操作超时");
            }
            catch (Exception ex)
            {
                LogStatus("读取设备信息", false, ex.Message);
                Debug.WriteLine($"读取设备信息失败: {ex}");
            }
        }

        private void UpdatePartitionList(List<PartitionInfo> partitions)
        {
            ResetPartitionHighlight();
            listView1.BeginUpdate();
            listView1.Items.Clear();
            UpdatePartitionListGridLines();
            // 用于调试的统计变量
            int fsDetectedCount = 0;
            
            foreach (var part in partitions)
            {
                var item = new ListViewItem(part.Name);
                item.SubItems.Add(part.Lun.ToString());
                item.SubItems.Add(FormatFileSize(part.Sectors * (ulong)part.SectorSize));
                item.SubItems.Add(part.StartLba.ToString());
                item.SubItems.Add(part.Sectors.ToString());
                
                // 文件系统类型和镜像格式
                string fsDisplay = part.FileSystemShort ?? "-";
                string fmtDisplay = part.ImageFormatShort ?? "-";
                string fileDisplay = part.FileName ?? "";
                
                // 统计检测到的文件系统
                if (part.FileSystem != PartitionFileSystem.Unknown && part.FileSystem != PartitionFileSystem.None)
                {
                    fsDetectedCount++;
                }
                
                item.SubItems.Add(fsDisplay);   // 索引 5: FS
                item.SubItems.Add(fmtDisplay);  // 索引 6: Fmt
                item.SubItems.Add(fileDisplay); // 索引 7: File
                
                item.Tag = part;
                ApplyPartitionFileState(item, part);
                
                // 根据文件系统类型设置颜色提示
                SetPartitionItemColor(item, part);
                
                listView1.Items.Add(item);
            }
            
            // 输出检测结果调试信息
            if (fsDetectedCount == 0)
            {
                AppendLog($"提示: 未检测到任何文件系统类型 (FS列显示 '-')", Color.Orange);
            }
            listView1.EndUpdate();
            UpdatePartitionListGridLines();
            
            if (partitions.Count > 0)
            {
                // 统计信息
                var fsCounts = partitions.GroupBy(p => p.FileSystem)
                    .ToDictionary(g => g.Key, g => g.Count());
                var formatCounts = partitions.GroupBy(p => p.ImageFormat)
                    .ToDictionary(g => g.Key, g => g.Count());
                
                int ext4Count = 0, erofsCount = 0, f2fsCount = 0, sparseCount = 0, rawCount = 0;
                fsCounts.TryGetValue(PartitionFileSystem.EXT4, out ext4Count);
                fsCounts.TryGetValue(PartitionFileSystem.EROFS, out erofsCount);
                fsCounts.TryGetValue(PartitionFileSystem.F2FS, out f2fsCount);
                formatCounts.TryGetValue(PartitionImageFormat.Sparse, out sparseCount);
                formatCounts.TryGetValue(PartitionImageFormat.Raw, out rawCount);
                
                LogStatus($"读取分区表成功，共 {partitions.Count} 个分区", true);
                
                // 显示文件系统统计 (如果有检测到)
                if (ext4Count > 0 || erofsCount > 0 || f2fsCount > 0)
                {
                    var fsInfo = new List<string>();
                    if (ext4Count > 0) fsInfo.Add($"EXT4:{ext4Count}");
                    if (erofsCount > 0) fsInfo.Add($"EROFS:{erofsCount}");
                    if (f2fsCount > 0) fsInfo.Add($"F2FS:{f2fsCount}");
                    LogKeyValue("文件系统", string.Join(", ", fsInfo), true);
                }
                
                // 显示格式统计
                if (sparseCount > 0 || rawCount > 0)
                {
                    LogKeyValue("镜像格式", $"Raw:{rawCount}, Sparse:{sparseCount}", true);
                }
            }
            else
            {
                LogStatus("读取分区表", false, "Firehose 限制读取");
                LogWarning("提示: 您可以使用 XML 刷写模式进行刷机");
            }
        }

        /// <summary>
        /// 根据分区属性设置 ListView 项的颜色
        /// </summary>
        private void SetPartitionItemColor(ListViewItem item, PartitionInfo part)
        {
            // 获取分区风险等级
            var riskLevel = GetPartitionRiskLevel(part.Name);
            
            // 设置整行背景色和文字颜色（基于风险等级）
            if (riskLevel != PartitionRiskLevel.Normal)
            {
                item.BackColor = PartitionRiskBackColors[riskLevel];
                item.ForeColor = PartitionRiskForeColors[riskLevel];
                
                // 为所有子项设置相同颜色
                foreach (ListViewItem.ListViewSubItem subItem in item.SubItems)
                {
                    subItem.BackColor = PartitionRiskBackColors[riskLevel];
                    subItem.ForeColor = PartitionRiskForeColors[riskLevel];
                }
            }
            
            // 文件系统类型列 (索引 5) 使用特殊颜色
            if (item.SubItems.Count > 5)
            {
                switch (part.FileSystem)
                {
                    case PartitionFileSystem.EROFS:
                        item.SubItems[5].ForeColor = Color.DarkCyan;
                        break;
                    case PartitionFileSystem.EXT4:
                        item.SubItems[5].ForeColor = Color.DarkGreen;
                        break;
                    case PartitionFileSystem.F2FS:
                        item.SubItems[5].ForeColor = Color.DarkBlue;
                        break;
                    case PartitionFileSystem.SquashFS:
                        item.SubItems[5].ForeColor = Color.DarkMagenta;
                        break;
                }
            }
            
            // 镜像格式列 (索引 6) - Sparse 格式用橙色
            if (item.SubItems.Count > 6 && part.ImageFormat == PartitionImageFormat.Sparse)
            {
                item.SubItems[6].ForeColor = Color.DarkOrange;
            }
        }

        /// <summary>
        /// 检测分区镜像文件的格式和文件系统类型
        /// </summary>
        private void DetectPartitionImageFormat(PartitionInfo partition, string imagePath)
        {
            if (!File.Exists(imagePath)) return;

            try
            {
                using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    // 文件太小，跳过
                    if (fs.Length < 28)
                    {
                        partition.ImageFormat = PartitionImageFormat.Raw;
                        partition.FileSystem = PartitionFileSystem.Unknown;
                        return;
                    }
                    
                    // super.img 需要读取更多数据 (LP 元数据后才是实际分区数据)
                    bool isSuperPartition = IsLpContainerPartition(partition.Name);
                    int readSize = isSuperPartition 
                        ? (int)Math.Min(2 * 1024 * 1024, fs.Length)  // super: 读取 2MB
                        : (int)Math.Min(16384, fs.Length);           // 其他: 读取 16KB
                    
                    byte[] header = br.ReadBytes(readSize);
                    
                    // 检测格式和文件系统
                    var (format, fileSystem) = DetectImageFormatAndFileSystem(header, partition.Name);
                    partition.ImageFormat = format;
                    partition.FileSystem = fileSystem;
                }
            }
            catch (Exception ex)
            {
                // 只在调试模式下显示警告，避免日志刷屏
                Debug.WriteLine($"检测 {partition.Name} 格式失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从数据头检测镜像格式和文件系统类型
        /// </summary>
        private (PartitionImageFormat, PartitionFileSystem) DetectImageFormatAndFileSystem(byte[] data, string partitionName = "")
        {
            if (data == null || data.Length < 28)
                return (PartitionImageFormat.Unknown, PartitionFileSystem.Unknown);

            // 常量定义
            const uint SPARSE_MAGIC = 0xED26FF3A;
            const uint LP_METADATA_GEOMETRY_MAGIC = 0x616c4467;  // "gDla" - super.img LP 格式
            const ushort SPARSE_CHUNK_RAW = 0xCAC1;
            const ushort SPARSE_CHUNK_FILL = 0xCAC2;
            const ushort SPARSE_CHUNK_DONT_CARE = 0xCAC3;

            PartitionImageFormat format = PartitionImageFormat.Raw;

            // 检查 Sparse 魔数 @ offset 0
            uint magic0 = BitConverter.ToUInt32(data, 0);
            if (magic0 == SPARSE_MAGIC)
            {
                format = PartitionImageFormat.Sparse;
                
                // super 分区是 LP 容器，需要深度扫描
                if (IsLpContainerPartition(partitionName))
                {
                    // 对 Sparse super.img 进行深度扫描
                    var innerFs = ScanLpContainerFileSystem(data);
                    return (format, innerFs);
                }
                
                // 解析 Sparse 头部以找到实际数据
                if (data.Length >= 28)
                {
                    ushort fileHdrSz = BitConverter.ToUInt16(data, 8);  // 文件头大小 (通常 28)
                    ushort chunkHdrSz = BitConverter.ToUInt16(data, 10); // chunk 头大小 (通常 12)
                    
                    // 跳过 Sparse 头部，查找第一个 RAW chunk
                    int offset = fileHdrSz;
                    int maxOffset = Math.Min(data.Length - 12, 8192); // 最多搜索前 8KB
                    int safetyCounter = 0; // 防止无限循环
                    
                    while (offset < maxOffset && offset >= fileHdrSz && safetyCounter < 100)
                    {
                        safetyCounter++;
                        if (offset + 12 > data.Length) break;
                        
                        ushort chunkType = BitConverter.ToUInt16(data, offset);
                        uint chunkTotalSz = BitConverter.ToUInt32(data, offset + 8);
                        
                        // 安全检查：chunkTotalSz 必须大于 0 且合理
                        if (chunkTotalSz == 0 || chunkTotalSz > int.MaxValue)
                            break;
                        
                        if (chunkType == SPARSE_CHUNK_RAW)
                        {
                            // 找到 RAW chunk，检测其中的文件系统
                            int rawDataOffset = offset + chunkHdrSz;
                            if (rawDataOffset + 1082 <= data.Length)
                            {
                                // 从 RAW chunk 数据中检测文件系统
                                var fs = DetectFileSystemFromRawData(data, rawDataOffset);
                                return (format, fs);
                            }
                            break;
                        }
                        else if (chunkType == SPARSE_CHUNK_FILL || chunkType == SPARSE_CHUNK_DONT_CARE)
                        {
                            // 跳过 FILL 和 DONT_CARE chunk (安全转换)
                            int chunkSize = (int)Math.Min(chunkTotalSz, int.MaxValue);
                            if (chunkSize <= 0) break; // 防止负数或零
                            offset += chunkSize;
                        }
                        else
                        {
                            // 未知 chunk 类型，停止搜索
                            break;
                        }
                    }
                }
                
                return (format, PartitionFileSystem.Unknown);
            }
            
            // 检查是否是 LP 容器格式 (Raw super.img)
            if (IsLpContainerPartition(partitionName))
            {
                // 对 super.img 进行深度扫描，检测内部逻辑分区的文件系统
                var innerFs = ScanLpContainerFileSystem(data);
                return (PartitionImageFormat.Raw, innerFs);
            }

            // Raw 镜像直接检测文件系统
            var fileSystem = DetectFileSystemFromRawData(data, 0);
            return (format, fileSystem);
        }
        
        /// <summary>
        /// 判断分区是否是 LP (Logical Partition) 容器
        /// </summary>
        private bool IsLpContainerPartition(string partitionName)
        {
            if (string.IsNullOrEmpty(partitionName)) return false;
            
            // super 分区是 LP 容器
            return partitionName.Equals("super", StringComparison.OrdinalIgnoreCase) ||
                   partitionName.StartsWith("super_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 对 super.img (LP 容器) 进行深度扫描，检测内部逻辑分区的文件系统
        /// </summary>
        private PartitionFileSystem ScanLpContainerFileSystem(byte[] data)
        {
            const uint LP_GEOMETRY_MAGIC = 0x616c4467;  // "gDla"
            const uint EROFS_MAGIC = 0xE0F5E1E2;
            const uint F2FS_MAGIC = 0xF2F52010;
            const ushort EXT4_MAGIC = 0xEF53;
            
            // 1. 首先尝试解析 LP 元数据找到第一个逻辑分区的偏移
            // LP Geometry 通常在 4096 或 8192 字节处
            int[] geometryOffsets = { 4096, 8192, 0 };
            long firstPartitionOffset = -1;
            
            foreach (int geomOffset in geometryOffsets)
            {
                if (geomOffset + 4 > data.Length) continue;
                
                uint magic = BitConverter.ToUInt32(data, geomOffset);
                if (magic == LP_GEOMETRY_MAGIC)
                {
                    // 找到 LP Geometry，尝试解析
                    // LP 元数据结构: Geometry (4KB) + Metadata (variable)
                    // 第一个逻辑分区通常在 1MB 或更后的位置
                    // 简化处理：从 1MB 开始扫描
                    firstPartitionOffset = 1024 * 1024; // 1MB
                    break;
                }
            }
            
            // 2. 如果没找到 LP Geometry，也从 1MB 开始扫描
            if (firstPartitionOffset < 0)
            {
                firstPartitionOffset = 1024 * 1024;
            }
            
            // 3. 从可能的起始位置开始扫描文件系统魔数
            // 扫描范围: 1MB - 2MB，每 4KB 对齐检查
            for (long offset = firstPartitionOffset; offset < data.Length - 1082; offset += 4096)
            {
                int intOffset = (int)offset;
                
                // 检查 EROFS @ offset + 1024
                if (intOffset + 1028 <= data.Length)
                {
                    uint erofsCheck = BitConverter.ToUInt32(data, intOffset + 1024);
                    if (erofsCheck == EROFS_MAGIC)
                        return PartitionFileSystem.EROFS;
                }
                
                // 检查 F2FS @ offset + 1024
                if (intOffset + 1028 <= data.Length)
                {
                    uint f2fsCheck = BitConverter.ToUInt32(data, intOffset + 1024);
                    if (f2fsCheck == F2FS_MAGIC)
                        return PartitionFileSystem.F2FS;
                }
                
                // 检查 EXT4 @ offset + 1080
                if (intOffset + 1082 <= data.Length)
                {
                    ushort ext4Check = BitConverter.ToUInt16(data, intOffset + 1080);
                    if (ext4Check == EXT4_MAGIC)
                        return PartitionFileSystem.EXT4;
                }
            }
            
            // 4. 如果以上都没找到，尝试更大步长扫描
            for (long offset = 0; offset < Math.Min(data.Length - 1082, 512 * 1024); offset += 65536)
            {
                int intOffset = (int)offset;
                
                // 检查 EROFS
                if (intOffset + 1028 <= data.Length)
                {
                    uint erofsCheck = BitConverter.ToUInt32(data, intOffset + 1024);
                    if (erofsCheck == EROFS_MAGIC)
                        return PartitionFileSystem.EROFS;
                }
                
                // 检查 EXT4
                if (intOffset + 1082 <= data.Length)
                {
                    ushort ext4Check = BitConverter.ToUInt16(data, intOffset + 1080);
                    if (ext4Check == EXT4_MAGIC)
                        return PartitionFileSystem.EXT4;
                }
            }
            
            return PartitionFileSystem.Unknown;
        }

        /// <summary>
        /// 从 Raw 数据中检测文件系统类型
        /// </summary>
        private PartitionFileSystem DetectFileSystemFromRawData(byte[] data, int baseOffset)
        {
            const uint EROFS_MAGIC = 0xE0F5E1E2;
            const uint F2FS_MAGIC = 0xF2F52010;
            const ushort EXT4_MAGIC = 0xEF53;
            const uint SQUASHFS_MAGIC = 0x73717368; // "hsqs" little-endian

            // 检查 EROFS 魔数 @ offset 1024
            if (baseOffset + 1028 <= data.Length)
            {
                uint erofsCheck = BitConverter.ToUInt32(data, baseOffset + 1024);
                if (erofsCheck == EROFS_MAGIC)
                    return PartitionFileSystem.EROFS;
            }

            // 检查 F2FS 魔数 @ offset 1024
            if (baseOffset + 1028 <= data.Length)
            {
                uint f2fsCheck = BitConverter.ToUInt32(data, baseOffset + 1024);
                if (f2fsCheck == F2FS_MAGIC)
                    return PartitionFileSystem.F2FS;
            }

            // 检查 EXT4 魔数 @ offset 1080 (1024 + 0x38)
            if (baseOffset + 1082 <= data.Length)
            {
                ushort ext4Check = BitConverter.ToUInt16(data, baseOffset + 1080);
                if (ext4Check == EXT4_MAGIC)
                    return PartitionFileSystem.EXT4;
            }

            // 检查 SquashFS 魔数 @ offset 0
            if (baseOffset + 4 <= data.Length)
            {
                uint squashCheck = BitConverter.ToUInt32(data, baseOffset);
                if (squashCheck == SQUASHFS_MAGIC)
                    return PartitionFileSystem.SquashFS;
            }

            return PartitionFileSystem.Unknown;
        }

        private void UpdatePartitionListGridLines()
        {
            if (listView1 == null)
            {
                return;
            }

            listView1.GridLines = listView1.Items.Count > 0;
        }

        private string ResolvePartitionFilePath(string storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(storedPath))
            {
                return storedPath;
            }

            if (string.IsNullOrWhiteSpace(currentFirmwareFolder))
            {
                return storedPath;
            }

            return Path.Combine(currentFirmwareFolder, storedPath);
        }

        private string GetRelativeFirmwarePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(currentFirmwareFolder))
            {
                return absolutePath;
            }

            try
            {
                string firmwareRoot = Path.GetFullPath(currentFirmwareFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string candidate = Path.GetFullPath(absolutePath);

                if (candidate.StartsWith(firmwareRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = candidate.Substring(firmwareRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return string.IsNullOrEmpty(relative) ? Path.GetFileName(candidate) : relative;
                }
            }
            catch
            {
            }

            return absolutePath;
        }

        private void ApplyPartitionFileState(ListViewItem item, PartitionInfo part)
        {
            if (item == null || part == null)
            {
                return;
            }

            string storedPath = part.FileName ?? string.Empty;
            // 确保有足够的 SubItems (索引 0-7: Name, Lun, Size, Start, Sectors, FS, Fmt, File)
            if (item.SubItems.Count < 8)
            {
                while (item.SubItems.Count < 8)
                {
                    item.SubItems.Add(string.Empty);
                }
            }

            string resolvedPath = ResolvePartitionFilePath(storedPath);
            bool fileExists = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath);

            // File 列现在是索引 7 (之前是 5，但增加了 FS 和 Fmt 列)
            const int FILE_COLUMN_INDEX = 7;

            if (string.IsNullOrEmpty(storedPath))
            {
                item.SubItems[FILE_COLUMN_INDEX].Text = string.Empty;
                item.Checked = false;
                item.ForeColor = Color.Black;
                return;
            }

            item.SubItems[FILE_COLUMN_INDEX].Text = fileExists ? storedPath : $"{storedPath} (缺失)";

            if (!fileExists)
            {
                item.Checked = false;
                item.ForeColor = Color.Gray;
                return;
            }

            bool isProtected = protectedPartitions.Contains(part.Name) || part.Lun == 5;
            if (part.Name.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isProtected = false;
            }

            if (checkbox6.Checked && isProtected)
            {
                item.Checked = false;
            }
            else
            {
                item.Checked = true;
            }

            item.ForeColor = Color.Black;
        }

        private string FormatFileSize(ulong bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private async void button6_Click_1(object sender, EventArgs e)
        {
            // 检查是否有分区数据（从设备读取或从 XML 加载）
            if (!isGptRead && !hasXmlPartitions)
            {
                ShowWarnMessage("请先读取分区表或加载固件 XML 后再进行操作");
                return;
            }

            // 写入操作通常针对单个分区，或者需要复杂的映射逻辑
            // 这里暂时保持单选逻辑，但使用新的 GetSelectedOrCheckedPartitions 获取第一个
            var partitions = GetSelectedOrCheckedPartitions();
            if (partitions.Count == 0)
            {
                ShowWarnMessage("请先选择一个分区 (点击行或勾选)");
                return;
            }
            var part = partitions[0]; // 取第一个

             if (!TryGetSerialForAction("写入分区", out string port)) return;
             if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            // var part = listView1.SelectedItems[0].Tag as PartitionInfo;
            if (part == null) return;

            bool isProtected = false;
            if (protectedPartitions.Contains(part.Name)) isProtected = true;
            if (part.Lun == 5) isProtected = true;
            if (part.Name.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0) isProtected = false;

            if (isProtected && checkbox6.Checked)
            {
                ShowErrorMessage($"分区 {part.Name} 受保护，禁止写入！");
                return;
            }

            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "镜像文件|*.img;*.bin;*.mbn;*.elf;*.hex|所有文件|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = $"选择写入 {part.Name} 的镜像";
                openFileDialog.RestoreDirectory = true;
                if (!string.IsNullOrEmpty(currentFirmwareFolder) && Directory.Exists(currentFirmwareFolder))
                {
                    openFileDialog.InitialDirectory = currentFirmwareFolder;
                }

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    _cts = new CancellationTokenSource();
                    var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
                    AuthType authType = GetAuthType();
                    var authFiles = await PrepareAuthFilesAsync();
                    if (!authFiles.ok) return;
                    var digest = authFiles.digest;
                    var signature = authFiles.signature;

                    var progPath = await EnsureProgrammerPathAsync();
                    if (string.IsNullOrEmpty(progPath)) return;

                    bool success = await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
                    {
                        var task = new FlashPartitionInfo
                        {
                            Name = part.Name,
                            Lun = part.Lun.ToString(),
                            StartSector = part.StartLbaStr,
                            NumSectors = (long)part.Sectors,
                            Filename = openFileDialog.FileName
                        };
                        
                        var patchesToApply = currentPatchFiles.Count > 0
                            ? new List<string>(currentPatchFiles)
                            : new List<string>();
                        await executor.ExecuteFlashTasksAsync(new List<FlashPartitionInfo> { task }, checkbox6.Checked, patchesToApply, _cts.Token);
                        
                        // [新增] 写入后自动激活 LUN (Patch 后自动激活)
                        try 
                        {
                            string type = executor.Client.StorageType;
                            if (type == "ufs" && (part.Lun == 1 || part.Lun == 2))
                            {
                                AppendLog($"[自动激活] 检测到写入 UFS LUN{part.Lun}，正在激活...", Color.Blue);
                                if (executor.Client.SetBootLun(part.Lun))
                                    AppendLog($"激活 LUN{part.Lun} 成功", Color.Green);
                                else
                                    AppendLog($"激活 LUN{part.Lun} 失败", Color.Orange);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"自动激活失败: {ex.Message}", Color.Orange);
                        }

                    }, _cts.Token);

                    if (success)
                    {
                        CleanupCloudLoaderIfNeeded();
                    }

                    if (success && checkbox3.Checked)
                    {
                        await deviceManager.RebootDevice(port);
                    }
                }
            }
        }

        private void CleanupCloudLoaderIfNeeded()
        {
            try
            {
                var loaderPath = input2?.Text;
                if (string.IsNullOrWhiteSpace(loaderPath)) return;

                string cloudRoot = Path.GetFullPath(Path.Combine(targetFolder, "cloud_loader"));
                string loaderFullPath = Path.GetFullPath(loaderPath);

                if (!loaderFullPath.StartsWith(cloudRoot, StringComparison.OrdinalIgnoreCase)) return;

                string? chipFolder = Path.GetDirectoryName(loaderFullPath);
                if (string.IsNullOrWhiteSpace(chipFolder) || !Directory.Exists(chipFolder)) return;

                try
                {
                    Directory.Delete(chipFolder, true);
                }
                catch (IOException)
                {
                    Task.Delay(200).Wait();
                    Directory.Delete(chipFolder, true);
                }

                // 如果 cloud_loader 已空，顺便删除
                if (Directory.Exists(cloudRoot) &&
                    Directory.GetDirectories(cloudRoot).Length == 0 &&
                    Directory.GetFiles(cloudRoot).Length == 0)
                {
                    Directory.Delete(cloudRoot, false);
                }

                input2.Text = string.Empty;
                input3.Text = string.Empty;
                input4.Text = string.Empty;

                AppendLog("云端引导临时文件已清理", Color.Green);
            }
            catch (Exception ex)
            {
                AppendLog($"云端引导清理失败: {ex.Message}", Color.Orange);
            }
        }

        private async void button7_Click(object sender, EventArgs e)
        {
            // 检查是否有分区数据（从设备读取或从 XML 加载）
            if (!isGptRead && !hasXmlPartitions)
            {
                ShowWarnMessage("请先读取分区表或加载固件 XML 后再进行操作");
                return;
            }

            var partitions = GetSelectedOrCheckedPartitions();
            if (partitions.Count == 0)
            {
                ShowWarnMessage("请先选择至少一个分区 (点击行或勾选)");
                return;
            }

             if (!TryGetSerialForAction("擦除分区", out string port)) return;
             if (!port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("请先进入 9008 (EDL) 模式", Color.Red);
                return;
            }

            // 检查保护分区
            foreach (var part in partitions)
            {
                bool isProtected = false;
                if (protectedPartitions.Contains(part.Name)) isProtected = true;
                if (part.Lun == 5) isProtected = true;
                if (part.Name.IndexOf("gpt", StringComparison.OrdinalIgnoreCase) >= 0) isProtected = false;

                if (isProtected && checkbox6.Checked)
                {
                    ShowErrorMessage($"分区 {part.Name} 受保护，禁止擦除！");
                    return;
                }
            }

            if (MessageBox.Show($"确定要擦除选中的 {partitions.Count} 个分区吗？", "警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            bool success = await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
            {
                foreach (var part in partitions)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    await executor.ErasePartitionAsync(part, _cts.Token);
                }
            }, _cts.Token);

            if (success && checkbox3.Checked)
            {
                await deviceManager.RebootDevice(port);
            }
        }

        private AuthType GetAuthType()
        {
            if (checkbox5.Checked) return AuthType.Vip;
            if (select2.Text == "VIP模式") return AuthType.Vip;
            if (select2.Text == "MI免授权") return AuthType.Xiaomi;
            return AuthType.Standard;
        }

        private void select3_SelectedIndexChanged(object sender, AntdUI.IntEventArgs e)
        {
            try
            {
                // 直接获取当前显示的文本并移除前缀
                string selectedText = StripDeviceStatusPrefix(select3.Text);

                if (!string.IsNullOrWhiteSpace(selectedText) && !selectedText.Contains("System.Object"))
                {
                    var parts = selectedText.Split('|');
                    if (parts.Length == 2)
                    {
                        string modePart = parts[0].Trim();
                        string identifier = parts[1].Trim();

                        string deviceLabel;
                        if (modePart.EndsWith("模式", StringComparison.Ordinal))
                        {
                            deviceLabel = modePart.Substring(0, modePart.Length - 2) + "设备";
                        }
                        else if (!modePart.EndsWith("设备", StringComparison.Ordinal))
                        {
                            deviceLabel = modePart + "设备";
                        }
                        else
                        {
                            deviceLabel = modePart;
                        }

                        AppendLog($"已选择{deviceLabel} -> {identifier}", Color.Green);
                    }
                    else
                    {
                        AppendLog($"用户选择了设备: {selectedText}", Color.Purple);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"选择设备时出错: {ex.Message}", Color.Red);
            }
        }

        private void select2_SelectedIndexChanged(object sender, AntdUI.IntEventArgs e)
        {
            // 保存原始布局信息
            if (!isOriginalLayoutSaved)
            {
                originalInput5Location = input5.Location;
                originalButton2Location = button2.Location;
                originalCheckbox2Location = checkbox2.Location;
                originalListView1Size = listView1.Size;
                originalListView1Location = listView1.Location;
                originalInput7Size = input7.Size;
                originalInput7Location = input7.Location;
                isOriginalLayoutSaved = true;
            }

            switch (select2.Text)
            {
                case "VIP模式":
                    checkbox5.Checked = true;
                    // 恢复原始布局
                    if (!tabPage1.Controls.Contains(input3))
                    {
                        tabPage1.Controls.Add(input3);
                        tabPage1.Controls.Add(input4);
                    }
                    
                    // 恢复原始位置
                    input5.Location = originalInput5Location;
                    button2.Location = originalButton2Location;
                    checkbox2.Location = originalCheckbox2Location;
                    
                    // 恢复原始大小和位置
                    listView1.Location = originalListView1Location;
                    listView1.Size = originalListView1Size;
                    input7.Location = originalInput7Location;
                    input7.Size = originalInput7Size;
                    break;
                
                case "MI免授权":
                case "通用模式QC":
                    checkbox5.Checked = false;
                    // 移除input3和input4
                    tabPage1.Controls.Remove(input3);
                    tabPage1.Controls.Remove(input4);
                    
                    // 上移input5，button2，checkbox2
                    input5.Location = new Point(originalInput5Location.X, originalInput5Location.Y - 32);
                    button2.Location = new Point(originalButton2Location.X, originalButton2Location.Y - 32);
                    checkbox2.Location = new Point(originalCheckbox2Location.X, originalCheckbox2Location.Y - 32);
                    
                    // 调整listView1和input7的大小和位置
                    listView1.Location = new Point(originalListView1Location.X, originalListView1Location.Y - 32);
                    listView1.Size = new Size(originalListView1Size.Width, originalListView1Size.Height + 32);
                    input7.Location = new Point(originalInput7Location.X, originalInput7Location.Y - 32);
                    input7.Size = new Size(originalInput7Size.Width, originalInput7Size.Height + 32);
                    break;
            }
        }
        /// <summary>
        /// 异步启动进程
        /// </summary>
        /// <param name="fileName">文件名或路径</param>
        /// <param name="workingDirectory">工作目录</param>
        /// <returns>启动是否成功</returns>
        private async Task<bool> StartProcessAsync(string fileName, string? arguments = null, string? workingDirectory = null)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName))
                    return false;

                string dir = workingDirectory
                             ?? SystemDirectory
                             ?? Environment.CurrentDirectory
                             ?? ".";

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = true,
                    WorkingDirectory = dir
                };

                // 添加参数支持
                if (!string.IsNullOrEmpty(arguments))
                {
                    startInfo.Arguments = arguments;
                }

                await Task.Run(() =>
                {
                    Process.Start(startInfo);
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动进程失败: {ex.Message}");
                return false;
            }
        }
        // 异步版本的事件处理方法
        private async void 设备管理器ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                // 异步执行
                _result = await StartProcessAsync("devmgmt.msc");

                // 可根据_result显示相应提示
                if (!_result)
                {
                    // 处理失败情况
                    ShowErrorMessage("无法启动任务管理器");
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"操作失败: {ex.Message}");
            }
        }

        private async void 打开ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(targetFolder))
            {
                ShowErrorMessage("目标文件夹未设置");
                return;
            }

            _result = await StartProcessAsync("cmd.exe", @"/k ""color 04""", targetFolder);

            if (!_result)
            {
                ShowErrorMessage("无法启动命令提示符");
            }
        }

        private void GetVipAuthFiles(out string digest, out string signature)
        {
            digest = string.Empty;
            signature = string.Empty;
            if (GetAuthType() == AuthType.Vip)
            {
                var digestPath = GetInputStoredPath(input3);
                var sigPath = GetInputStoredPath(input4);
                if (!string.IsNullOrWhiteSpace(digestPath)) digest = digestPath!;
                if (!string.IsNullOrWhiteSpace(sigPath)) signature = sigPath!;
            }
        }

        private async Task<(bool ok, string digest, string signature)> PrepareAuthFilesAsync()
        {
            var authType = GetAuthType();
            GetVipAuthFiles(out string digest, out string signature);

            if (authType != AuthType.Vip)
            {
                return (true, digest, signature);
            }

            var ready = true; // 云端逻辑移除，默认ready (如果文件已选择)
            
            // 简单检查文件是否选择
            if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(signature))
            {
                // 如果是 VIP 模式且没有选择文件，提示用户
                // 但这里不做强制检查，让后续逻辑处理，或者在这里返回 false
                // 根据之前的 EnsureDeferredAuthFilesAsync 逻辑，如果没下载且没选择，会返回 false
                // 这里我们假设用户必须手动选择
                if (string.IsNullOrEmpty(digest) || string.IsNullOrEmpty(signature))
                {
                    ShowWarnMessage("VIP 模式需要选择 Digest 和 Signature 文件");
                    ready = false;
                }
            }

            return (ready, digest, signature);
        }

        private void SelectFile(AntdUI.Input input, string filter)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = filter;
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = "选择文件或文件夹";
                openFileDialog.RestoreDirectory = true;
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    SetInputStoredPath(input, openFileDialog.FileName);
                }
            }
        }
        private string? SelectDirectoryWithFileDialog(string title)
        {
            // 优化：使用 FolderBrowserDialog 替代 OpenFileDialog
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = title;
                dialog.ShowNewFolderButton = true;
                
                // 设置默认路径为桌面或上次使用的路径
                string defaultPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!string.IsNullOrEmpty(currentFirmwareFolder) && Directory.Exists(currentFirmwareFolder))
                {
                    defaultPath = currentFirmwareFolder;
                }
                dialog.SelectedPath = defaultPath;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.SelectedPath;
                }
            }

            return null;
        }
        private void Input2_DoubleClick(object sender, EventArgs e)
        {
            SelectFile(input2, "Firehose Programmer (*.elf;*.melf;*.mbn;*.bin)|*.elf;*.melf;*.mbn;*.bin|All files (*.*)|*.*");
        }

        private void Input3_DoubleClick(object sender, EventArgs e)
        {
            SelectFile(input3, "Digest File (*.bin;*.mbn;*.elf)|*.bin;*.mbn;*.elf|All files (*.*)|*.*");
        }

        private void Input4_DoubleClick(object sender, EventArgs e)
        {
            SelectFile(input4, "Signature File (*.bin;*.mbn)|*.bin;*.mbn|All files (*.*)|*.*");
        }

        private string? GetProgrammerPath()
        {
            var storedPath = GetInputStoredPath(input2);
            if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
            {
                return storedPath;
            }
            return null;
        }

        // Ensure programmer path is ready
        private async Task<string?> EnsureProgrammerPathAsync()
        {
            var path = GetProgrammerPath();
            if (!string.IsNullOrEmpty(path)) return path;

            ShowWarnMessage("请先选择本地引导文件");
            return null;
        }
        private readonly List<FastbootListEntry> allFastbootItems = new List<FastbootListEntry>();



        private string FormatSize(ulong bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1}{1}", number, suffixes[counter]);
        }

        private void InitializeFastbootOptionCheckboxes()
        {
            fastbootOptionCheckboxes = new Dictionary<string, AntdUI.Checkbox>
            {
                [FastbootOptionAutoReboot] = checkbox21,
                [FastbootOptionSwitchSlotA] = checkbox13,
                [FastbootOptionKeepData] = checkbox18,
                [FastbootOptionLockBootloader] = checkbox19,
                [FastbootOptionEraseFrp] = checkbox9
            };

            foreach (var checkbox in fastbootOptionCheckboxes.Values)
            {
                if (checkbox == null)
                {
                    continue;
                }

                checkbox.CheckedChanged -= FastbootOptionCheckbox_CheckedChanged;
                checkbox.CheckedChanged += FastbootOptionCheckbox_CheckedChanged;
                checkbox.Enabled = true;
            }

            ResetFastbootOptionOverrides();

            checkbox22.Checked = false;
            checkbox22.Enabled = false;
            checkbox22.CheckedChanged += checkbox22_CheckedChanged;
        }

        private void SetAllFastbootItemsChecked(bool isChecked)
        {
            foreach (var entry in allFastbootItems)
            {
                entry.IsChecked = isChecked;
            }

            suppressListView2CheckEvents = true;
            if (listView2 != null)
            {
                foreach (ListViewItem item in listView2.Items)
                {
                    item.Checked = isChecked;
                }
            }
            suppressListView2CheckEvents = false;
            RefreshFastbootOptionStates();
        }

        private void SetFastbootOptionItemsChecked(string optionKey, bool isChecked)
        {
            if (string.IsNullOrEmpty(optionKey))
            {
                return;
            }

            fastbootOptionUserOverrides[optionKey] = isChecked;

            bool taskShouldBeChecked = optionKey == FastbootOptionKeepData ? !isChecked : isChecked;

            foreach (var entry in allFastbootItems)
            {
                if (entry.Payload is FastbootTask task && string.Equals(task.OptionKey, optionKey, StringComparison.OrdinalIgnoreCase))
                {
                    entry.IsChecked = taskShouldBeChecked;
                    task.IsChecked = taskShouldBeChecked;
                }
            }

            suppressListView2CheckEvents = true;
            if (listView2 != null)
            {
                foreach (ListViewItem item in listView2.Items)
                {
                    if (item.Tag is FastbootListEntry entry &&
                        entry.Payload is FastbootTask task &&
                        string.Equals(task.OptionKey, optionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Checked = taskShouldBeChecked;
                    }
                }
            }
            suppressListView2CheckEvents = false;

            RefreshFastbootOptionStates();
        }

        private void RefreshFastbootOptionStates()
        {
            if (fastbootOptionCheckboxes == null)
            {
                return;
            }

            suppressFastbootOptionEvents = true;
            foreach (var kvp in fastbootOptionCheckboxes)
            {
                if (kvp.Value == null)
                {
                    continue;
                }

                var matchingEntries = allFastbootItems
                    .Where(entry => entry.Payload is FastbootTask task && string.Equals(task.OptionKey, kvp.Key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingEntries.Count == 0)
                {
                    bool defaultValue = fastbootOptionDefaults.TryGetValue(kvp.Key, out bool configuredDefault) && configuredDefault;
                    bool overrideValue = fastbootOptionUserOverrides.TryGetValue(kvp.Key, out bool userValue)
                        ? userValue
                        : defaultValue;
                    kvp.Value.Checked = overrideValue;
                    continue;
                }

                bool shouldCheck;
                if (string.Equals(kvp.Key, FastbootOptionKeepData, StringComparison.OrdinalIgnoreCase))
                {
                    shouldCheck = matchingEntries.All(entry => !entry.IsChecked);
                }
                else
                {
                    shouldCheck = matchingEntries.Any(entry => entry.IsChecked);
                }
                fastbootOptionUserOverrides[kvp.Key] = shouldCheck;
                kvp.Value.Checked = shouldCheck;
            }
            suppressFastbootOptionEvents = false;

            UpdateListView2SelectAllCheckbox();
        }

        private void UpdateListView2SelectAllCheckbox()
        {
            if (checkbox22 == null)
            {
                return;
            }

            bool hasItems = listView2 != null && listView2.Items.Count > 0;
            bool allChecked = hasItems && listView2.Items.Cast<ListViewItem>().All(item => item.Checked);

            suppressFastbootSelectAllEvent = true;
            checkbox22.Enabled = hasItems;
            checkbox22.Checked = hasItems && allChecked;
            suppressFastbootSelectAllEvent = false;
        }

        private void checkbox22_CheckedChanged(object sender, EventArgs e)
        {
            if (suppressFastbootSelectAllEvent)
            {
                return;
            }

            SetAllFastbootItemsChecked(checkbox22.Checked);
        }

        private void listView2_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (suppressListView2CheckEvents)
            {
                return;
            }

            if (e.Item.Tag is FastbootListEntry entry)
            {
                entry.IsChecked = e.Item.Checked;
                if (entry.Payload is FastbootTask task)
                {
                    task.IsChecked = e.Item.Checked;
                }
            }

            RefreshFastbootOptionStates();
        }

        private void FastbootOptionCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (fastbootOptionCheckboxes == null || suppressFastbootOptionEvents)
            {
                return;
            }

            if (sender is AntdUI.Checkbox checkbox)
            {
                var kvp = fastbootOptionCheckboxes.FirstOrDefault(pair => ReferenceEquals(pair.Value, checkbox));
                if (string.IsNullOrEmpty(kvp.Key))
                {
                    return;
                }

                SetFastbootOptionItemsChecked(kvp.Key, checkbox.Checked);
            }
        }



        private string FastbootPath => Path.Combine(Application.StartupPath, "fastboot.exe");

        private bool IsFastbootDeviceConnected()
        {
            // 检查 select3 (设备下拉框) 的文本是否包含 "Fastboot"
            // 或者使用 deviceManager.DetectDeviceStatus() 重新检测
            // 这里我们简单检查 select3 的文本，因为它是由 DetectDeviceStatus 定期更新的
            if (select3.Text.Contains("Fastboot"))
            {
                return true;
            }
            
            // 如果 select3 没有显示，尝试一次快速检测
            var result = deviceManager.DetectDeviceStatus();
            foreach (var device in result.Devices)
            {
                if (device.DeviceType == "Fastboot")
                {
                    return true;
                }
            }
            return false;
        }


        private async void button17_Click(object sender, EventArgs e)
        {
            if (!IsFastbootDeviceConnected())
            {
                AppendFastbootLog("错误: 未检测到 Fastboot 设备，请检查连接。");
                return;
            }

            // lblPayloadInfo.Text = "正在读取设备分区表...";
            AppendFastbootLog("开始读取设备分区表...");
            allFastbootItems.Clear();
            ResetFastbootSearchFilter();
            RefreshFastbootListViewItems();
            RefreshFastbootOptionStates();
            if (currentPayload != null) 
            {
                currentPayload.Dispose();
                currentPayload = null;
            }

            await Task.Run(() =>
            {
                string output = deviceManager.RunCommand($"\"{FastbootPath}\" getvar all");
                AppendFastbootLog("执行命令: fastboot getvar all");
                // Parse output
                // (bootloader) partition-size:name: 0xSize
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var partitions = new Dictionary<string, ulong>();

                foreach (var line in lines)
                {
                    if (line.Contains("partition-size:"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 3)
                        {
                            string name = parts[1].Trim();
                            string sizeHex = parts[2].Trim();
                            if (sizeHex.StartsWith("0x")) sizeHex = sizeHex.Substring(2);
                            if (ulong.TryParse(sizeHex, System.Globalization.NumberStyles.HexNumber, null, out ulong size))
                            {
                                partitions[name] = size;
                            }
                        }
                    }
                }

                this.Invoke((MethodInvoker)delegate {
                    allFastbootItems.Clear();
                    foreach (var kvp in partitions)
                    {
                        allFastbootItems.Add(new FastbootListEntry
                        {
                            Name = kvp.Key,
                            Size = FormatSize(kvp.Value),
                            Source = "Device",
                            IsChecked = false,
                            Payload = "DEVICE"
                        });
                    }
                    RefreshFastbootListViewItems();
                    AppendFastbootLog($"读取完成，共找到 {partitions.Count} 个分区。");
                    RefreshFastbootOptionStates();
                });
            });
        }

        private async void button15_Click(object sender, EventArgs e)
        {
            if (!IsFastbootDeviceConnected())
            {
                AppendFastbootLog("错误: 未检测到 Fastboot 设备，请检查连接。");
                return;
            }

            if (listView2.SelectedItems.Count == 0)
            {
                AppendFastbootLog("提示: 请选择要写入的分区。");
                return;
            }

            if (listView2.SelectedItems[0].Tag is not FastbootListEntry selectedEntry)
            {
                AppendFastbootLog("选择项无效。");
                return;
            }

            string partitionName = selectedEntry.Name;
            
            if (currentPayload != null && selectedEntry.Payload is PartitionUpdate partition)
            {
                // Flash from Payload
                // if (MessageBox.Show($"确定要将 Payload 中的 {partitionName} 写入设备吗？", "确认写入", MessageBoxButtons.YesNo) != DialogResult.Yes)
                //    return;

                // lblPayloadInfo.Text = $"正在写入 {partitionName}...";
                AppendFastbootLog($"开始写入分区: {partitionName} (来自 Payload)");
                try
                {
                    await Task.Run(() =>
                    {
                        string tempFile = Path.Combine(Path.GetTempPath(), partitionName + ".img");
                        var ex = currentPayload.extract(partitionName, Path.GetTempPath(), false, false);
                        if (ex != null) throw ex;

                        AppendFastbootLog($"已提取镜像到: {tempFile}");
                        string output = deviceManager.RunCommand($"\"{FastbootPath}\" flash {partitionName} \"{tempFile}\"");
                        AppendFastbootLog(output);
                        File.Delete(tempFile);
                    });
                    AppendFastbootLog("写入完成！");
                    // lblPayloadInfo.Text = "写入完成。";
                    AppendFastbootLog($"写入 {partitionName} 完成。");
                }
                catch (Exception ex)
                {
                    AppendFastbootLog("写入失败: " + ex.Message);
                    // lblPayloadInfo.Text = "写入失败。";
                    AppendFastbootLog($"写入失败: {ex.Message}");
                }
            }
            else
            {
                // Flash from File
                using (OpenFileDialog openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Filter = "镜像文件|*.img;*.bin;*.mbn;*.elf;*.hex|所有文件|*.*";
                    openFileDialog.FilterIndex = 1;
                    openFileDialog.Title = $"选择要写入 {partitionName} 的镜像文件";
                    openFileDialog.RestoreDirectory = true;
                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        // lblPayloadInfo.Text = $"正在写入 {partitionName}...";
                        AppendFastbootLog($"开始写入分区: {partitionName} (来自文件: {openFileDialog.FileName})");
                        try
                        {
                            await Task.Run(() =>
                            {
                                string output = deviceManager.RunCommand($"\"{FastbootPath}\" flash {partitionName} \"{openFileDialog.FileName}\"");
                                AppendFastbootLog(output);
                            });
                            AppendFastbootLog("写入完成！");
                            // lblPayloadInfo.Text = "写入完成。";
                            AppendFastbootLog($"写入 {partitionName} 完成。");
                        }
                        catch (Exception ex)
                        {
                            AppendFastbootLog("写入失败: " + ex.Message);
                            // lblPayloadInfo.Text = "写入失败。";
                            AppendFastbootLog($"写入失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        private async void button14_Click(object sender, EventArgs e)
        {
            if (!IsFastbootDeviceConnected())
            {
                AppendFastbootLog("错误: 未检测到 Fastboot 设备，请检查连接。");
                return;
            }

            if (listView2.SelectedItems.Count == 0)
            {
                AppendFastbootLog("提示: 请选择要擦除的分区。");
                return;
            }

            string partitionName = listView2.SelectedItems[0].Text;
            // if (MessageBox.Show($"确定要擦除分区 {partitionName} 吗？此操作不可逆！", "危险操作", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            //    return;

            // lblPayloadInfo.Text = $"正在擦除 {partitionName}...";
            AppendFastbootLog($"开始擦除分区: {partitionName}");
            try
            {
                await Task.Run(() =>
                {
                    string output = deviceManager.RunCommand($"\"{FastbootPath}\" erase {partitionName}");
                    AppendFastbootLog(output);
                });
                AppendFastbootLog("擦除完成！");
                // lblPayloadInfo.Text = "擦除完成。";
                AppendFastbootLog($"擦除 {partitionName} 完成。");
            }
            catch (Exception ex)
            {
                AppendFastbootLog("擦除失败: " + ex.Message);
                // lblPayloadInfo.Text = "擦除失败。";
                AppendFastbootLog($"擦除失败: {ex.Message}");
            }
        }



        private void select5_TextChanged(object sender, EventArgs e)
        {
            string newText = select5?.Text?.Trim() ?? string.Empty;
            if (!string.Equals(fastbootSearchText, newText, StringComparison.OrdinalIgnoreCase))
            {
                fastbootSearchText = newText;
            }
            RefreshFastbootListViewItems();
            RefreshFastbootOptionStates();
        }

        private void button18_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Batch Files (*.bat)|*.bat|All Files (*.*)|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = "选择文件或文件夹";
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string filePath = openFileDialog.FileName;
                    input10.Text = filePath;
                    ParseBatFile(filePath);
                }
            }
        }

        private class FastbootTask
        {
            public string Type { get; set; } // flash, erase, set_active, reboot, oem
            public string Partition { get; set; }
            public string Path { get; set; }
            public string OptionKey { get; set; }
            public bool IsChecked { get; set; }
        }

        private class FastbootListEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Size { get; set; } = "-";
            public string Source { get; set; } = string.Empty;
            public bool IsChecked { get; set; }
            public object Payload { get; set; }
        }

        private void RefreshFastbootListViewItems()
        {
            if (listView2 == null)
            {
                return;
            }

            string searchText = fastbootSearchText;
            IEnumerable<FastbootListEntry> viewItems = string.IsNullOrEmpty(searchText)
                ? allFastbootItems
                : allFastbootItems.Where(entry => entry.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);

            suppressListView2CheckEvents = true;
            listView2.BeginUpdate();
            listView2.Items.Clear();
            foreach (var entry in viewItems)
            {
                var item = new ListViewItem(entry.Name);
                item.SubItems.Add(entry.Size);
                item.SubItems.Add(entry.Source);
                item.Checked = entry.IsChecked;
                item.Tag = entry;
                listView2.Items.Add(item);
            }
            listView2.EndUpdate();
            suppressListView2CheckEvents = false;

            UpdateFastbootListGridLines();

            UpdateListView2SelectAllCheckbox();
        }

        private void ResetFastbootSearchFilter()
        {
            if (string.IsNullOrEmpty(fastbootSearchText))
            {
                return;
            }

            fastbootSearchText = string.Empty;
            if (select5 != null && !string.IsNullOrEmpty(select5.Text))
            {
                select5.Text = string.Empty;
            }
        }

        private void UpdateFastbootListGridLines()
        {
            if (listView2 == null)
            {
                return;
            }

            listView2.GridLines = listView2.Items.Count > 0;
        }

        private void ResetFastbootOptionOverrides(bool updateCheckboxes = false)
        {
            suppressFastbootOptionEvents = true;

            foreach (var kvp in fastbootOptionDefaults)
            {
                fastbootOptionUserOverrides[kvp.Key] = kvp.Value;

                if (updateCheckboxes && fastbootOptionCheckboxes != null && fastbootOptionCheckboxes.TryGetValue(kvp.Key, out var checkbox) && checkbox != null)
                {
                    checkbox.Checked = kvp.Value;
                }
            }

            suppressFastbootOptionEvents = false;
        }

        private void ParseBatFile(string filePath)
        {
            ResetFastbootOptionOverrides(updateCheckboxes: true);

            try
            {
                string[] lines = File.ReadAllLines(filePath);
                allFastbootItems.Clear();
                ResetFastbootSearchFilter();
                RefreshFastbootListViewItems();
                RefreshFastbootOptionStates();
                string workingDir = Path.GetDirectoryName(filePath) ?? string.Empty;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) ||
                        trimmedLine.StartsWith("REM", StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.StartsWith("::") ||
                        trimmedLine.StartsWith("if", StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.StartsWith("echo", StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.StartsWith("set", StringComparison.OrdinalIgnoreCase) ||
                        trimmedLine.StartsWith("for", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int errorCheckIndex = trimmedLine.IndexOf("||");
                    if (errorCheckIndex >= 0)
                    {
                        trimmedLine = trimmedLine.Substring(0, errorCheckIndex).Trim();
                    }

                    int redirectIndex = trimmedLine.IndexOf(">");
                    if (redirectIndex >= 0)
                    {
                        trimmedLine = trimmedLine.Substring(0, redirectIndex).Trim();
                    }

                    if (!trimmedLine.StartsWith("fastboot", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string args = trimmedLine.Substring(8).Trim();
                    if (args.StartsWith("%*"))
                    {
                        args = args.Substring(2).Trim();
                    }

                    string[] parts = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0)
                    {
                        continue;
                    }

                    string command = parts[0].ToLowerInvariant();

                    FastbootListEntry entry = null;

                    if (command == "flash" && parts.Length >= 3)
                    {
                        string partition = parts[1];
                        string optionKey = null;
                        if (string.Equals(partition, "userdata", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(partition, "metadata", StringComparison.OrdinalIgnoreCase))
                        {
                            optionKey = FastbootOptionKeepData;
                        }
                        int partIndex = args.IndexOf(partition, StringComparison.OrdinalIgnoreCase);
                        string pathPart = args.Substring(partIndex + partition.Length).Trim();
                        string path = pathPart.Trim('"');

                        if (path.Contains("%~dp0"))
                        {
                            path = path.Replace("%~dp0", workingDir + Path.DirectorySeparatorChar);
                        }
                        else if (!Path.IsPathRooted(path))
                        {
                            path = Path.Combine(workingDir, path);
                        }

                        path = path.Replace('/', Path.DirectorySeparatorChar);

                        long size = 0;
                        if (File.Exists(path))
                        {
                            size = new FileInfo(path).Length;
                        }

                        var task = new FastbootTask { Type = "flash", Partition = partition, Path = path, OptionKey = optionKey, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = partition,
                            Size = FormatSize((ulong)size),
                            Source = path,
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "erase" && parts.Length >= 2)
                    {
                        string partition = parts[1];
                        string optionKey = null;
                        if (string.Equals(partition, "frp", StringComparison.OrdinalIgnoreCase))
                        {
                            optionKey = FastbootOptionEraseFrp;
                        }
                        else if (string.Equals(partition, "userdata", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(partition, "metadata", StringComparison.OrdinalIgnoreCase))
                        {
                            optionKey = FastbootOptionKeepData;
                        }

                        var task = new FastbootTask { Type = "erase", Partition = partition, Path = string.Empty, OptionKey = optionKey, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = partition,
                            Size = "-",
                            Source = "(擦除)",
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "set_active" && parts.Length >= 2)
                    {
                        string slot = parts[1];
                        string optionKey = string.Equals(slot, "a", StringComparison.OrdinalIgnoreCase) ? FastbootOptionSwitchSlotA : null;
                        var task = new FastbootTask { Type = "set_active", Partition = string.Empty, Path = slot, OptionKey = optionKey, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = "set_active",
                            Size = "-",
                            Source = $"Slot {slot}",
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "reboot")
                    {
                        var task = new FastbootTask { Type = "reboot", Partition = string.Empty, Path = string.Empty, OptionKey = FastbootOptionAutoReboot, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = "reboot",
                            Size = "-",
                            Source = "(重启设备)",
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "oem" && parts.Length >= 2)
                    {
                        string oemCmd = parts[1];
                        string optionKey = string.Equals(oemCmd, "lock", StringComparison.OrdinalIgnoreCase) ? FastbootOptionLockBootloader : null;
                        var task = new FastbootTask { Type = "oem", Partition = oemCmd, Path = string.Empty, OptionKey = optionKey, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = "oem " + oemCmd,
                            Size = "-",
                            Source = "(OEM指令)",
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "flashing" && parts.Length >= 2)
                    {
                        string flashingCommand = parts[1];
                        string optionKey = string.Equals(flashingCommand, "lock", StringComparison.OrdinalIgnoreCase) ? FastbootOptionLockBootloader : null;
                        var task = new FastbootTask { Type = "flashing", Partition = flashingCommand, Path = string.Empty, OptionKey = optionKey, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = $"flashing {flashingCommand}",
                            Size = "-",
                            Source = $"(flashing {flashingCommand})",
                            IsChecked = true,
                            Payload = task
                        };
                    }
                    else if (command == "-w")
                    {
                        var task = new FastbootTask { Type = "wipe", Partition = "userdata", Path = string.Empty, OptionKey = FastbootOptionKeepData, IsChecked = true };
                        entry = new FastbootListEntry
                        {
                            Name = "-w",
                            Size = "-",
                            Source = "(清除数据)",
                            IsChecked = true,
                            Payload = task
                        };
                    }

                    if (entry != null)
                    {
                        allFastbootItems.Add(entry);
                    }
                }

                if (allFastbootItems.Count == 0)
                {
                    AppendFastbootLog("未在批处理文件中找到有效的 fastboot flash/erase 等命令。");
                }
                else
                {
                    AppendFastbootLog($"成功解析批处理文件，共找到 {allFastbootItems.Count} 个任务。");
                }

                RefreshFastbootListViewItems();
                RefreshFastbootOptionStates();
            }
            catch (Exception ex)
            {
                AppendFastbootLog($"解析批处理文件失败: {ex.Message}");
            }
        }

        private async void btn_flash_all_Click(object sender, EventArgs e)
        {
            if (!IsFastbootDeviceConnected())
            {
                AppendFastbootLog("未检测到 Fastboot 设备，请检查连接。");
                return;
            }

            if (allFastbootItems.Count == 0)
            {
                AppendFastbootLog("列表为空，请先加载刷机包。");
                return;
            }

            var runnableEntries = allFastbootItems
                .Where(entry => entry.IsChecked && entry.Payload is FastbootTask)
                .ToList();

            if (runnableEntries.Count == 0)
            {
                AppendFastbootLog("没有需要执行的任务，请先勾选要运行的命令。");
                return;
            }

            string previousSearch = fastbootSearchText;
            bool searchCleared = false;
            if (!string.IsNullOrEmpty(previousSearch))
            {
                ResetFastbootSearchFilter();
                RefreshFastbootListViewItems();
                searchCleared = true;
            }

            AppendFastbootLog("开始一键刷入...");

            int successCount = 0;
            int failCount = 0;

            foreach (var entry in runnableEntries)
            {
                if (entry.Payload is not FastbootTask task)
                {
                    continue;
                }

                ListViewItem matchedItem = null;
                if (listView2 != null)
                {
                    foreach (ListViewItem candidate in listView2.Items)
                    {
                        if (ReferenceEquals(candidate.Tag, entry))
                        {
                            matchedItem = candidate;
                            break;
                        }
                    }
                }

                if (matchedItem != null)
                {
                    matchedItem.Selected = true;
                    listView2.EnsureVisible(matchedItem.Index);
                }

                bool success = false;
                try
                {
                    if (task.Type == "flash")
                    {
                        if (!File.Exists(task.Path))
                        {
                            AppendFastbootLog($"错误: 文件不存在 - {task.Path}");
                            failCount++;
                            continue;
                        }

                        AppendFastbootLog($"正在刷入 {task.Partition}...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" flash {task.Partition} \"{task.Path}\"");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                    else if (task.Type == "erase")
                    {
                        AppendFastbootLog($"正在擦除 {task.Partition}...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" erase {task.Partition}");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                    else if (task.Type == "set_active")
                    {
                        AppendFastbootLog($"正在设置活动槽位: {task.Path}...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" set_active {task.Path}");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                    else if (task.Type == "reboot")
                    {
                        AppendFastbootLog("正在重启设备...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" reboot");
                            AppendFastbootLog(output);
                            success = true;
                        });
                    }
                    else if (task.Type == "oem")
                    {
                        AppendFastbootLog($"正在执行 OEM 指令: {task.Partition}...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" oem {task.Partition}");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                    else if (task.Type == "flashing")
                    {
                        AppendFastbootLog($"正在执行 fastboot flashing {task.Partition}...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" flashing {task.Partition}");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                    else if (task.Type == "wipe")
                    {
                        AppendFastbootLog("正在执行 fastboot -w...");
                        await Task.Run(() =>
                        {
                            string output = deviceManager.RunCommand($"\"{FastbootPath}\" -w");
                            AppendFastbootLog(output);
                            if (!output.Contains("error") && !output.Contains("FAILED")) success = true;
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppendFastbootLog($"执行异常: {ex.Message}");
                }

                if (success)
                {
                    AppendFastbootLog($"任务 {task.Type} {task.Partition} 成功。");
                    successCount++;
                }
                else
                {
                    AppendFastbootLog($"任务 {task.Type} {task.Partition} 失败。");
                    failCount++;
                }
            }

            AppendFastbootLog($"一键刷入完成。成功: {successCount}, 失败: {failCount}");

            if (searchCleared)
            {
                fastbootSearchText = previousSearch;
                if (select5 != null)
                {
                    select5.Text = previousSearch;
                }
                RefreshFastbootListViewItems();
                RefreshFastbootOptionStates();
            }
        }

        private void ApplyPartitionHighlight(ListViewItem targetItem)
        {
            if (targetItem == null)
            {
                return;
            }

            if (!ReferenceEquals(lastHighlightedItem, targetItem))
            {
                ResetPartitionHighlight();

                lastHighlightedItem = targetItem;
                lastHighlightBackColor = targetItem.BackColor;
                lastHighlightForeColor = targetItem.ForeColor;
            }

            targetItem.BackColor = partitionHighlightColor;
            targetItem.ForeColor = partitionHighlightTextColor;
        }

        private void ResetPartitionHighlight()
        {
            if (lastHighlightedItem == null)
            {
                return;
            }

            try
            {
                lastHighlightedItem.BackColor = lastHighlightBackColor.IsEmpty ? listView1.BackColor : lastHighlightBackColor;
                lastHighlightedItem.ForeColor = lastHighlightForeColor.IsEmpty ? listView1.ForeColor : lastHighlightForeColor;
            }
            catch
            {
                // ignore highlight reset failures
            }

            lastHighlightedItem = null;
            lastHighlightBackColor = Color.Empty;
            lastHighlightForeColor = Color.Empty;
        }

        private void Select1_TextChanged(object sender, EventArgs e)
        {
            // 实时搜索功能
            SearchAndShowPartitions();
        }

        private void select1_SelectedIndexChanged(object sender, IntEventArgs e)
        {
            // 当下拉选项被选择时，导航到对应的分区
            // AntdUI Select 使用 Text 属性获取选中的值
            string selectedPartitionName = select1.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(selectedPartitionName))
            {
                NavigateToPartition(selectedPartitionName);
            }
        }
        private void SearchAndShowPartitions()
        {
            if (_isOperationInProgress) return;
            if (listView1.Items.Count == 0) 
            {
                AppendLog("请先读取分区表", Color.Orange);
                return;
            }

            string searchText = select1.Text?.Trim() ?? string.Empty;

            // 如果搜索文本为空，清空下拉列表
            if (string.IsNullOrEmpty(searchText))
            {
                if (select1.Items != null) select1.Items.Clear();
                return;
            }

            // 搜索匹配的分区（针对分区名称列，不区分大小写）
            var matchedItems = listView1.Items.Cast<ListViewItem>()
                .Select(item => item.Text)
                .Where(name => !string.IsNullOrEmpty(name) && name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // 更新select1的下拉选项
            if (select1.Items != null)
            {
                select1.Items.Clear();
                if (matchedItems.Length > 0)
                {
                    select1.Items.AddRange(matchedItems.Cast<object>().ToArray());
                    AppendLog($"找到 {matchedItems.Length} 个匹配的分区", Color.DarkBlue);
                }
                else
                {
                    AppendLog($"未找到包含 '{searchText}' 的分区", Color.Orange);
                }
            }
        }

        private void NavigateToPartition(string partitionName)
        {
            if (_isOperationInProgress) return;
            if (listView1.Items.Count == 0) return;

            // 在ListView中查找对应的分区（匹配名称列）
            var targetItem = listView1.Items.Cast<ListViewItem>()
                .FirstOrDefault(item => string.Equals(item.Text, partitionName, StringComparison.OrdinalIgnoreCase));

            if (targetItem != null)
            {
                // 取消所有选中状态
                foreach (ListViewItem item in listView1.Items)
                {
                    item.Selected = false;
                }

                // 选中目标分区并滚动到可视区域
                targetItem.Selected = true;
                targetItem.Focused = true;
                targetItem.EnsureVisible();
                ApplyPartitionHighlight(targetItem);

                AppendLog($"已导航到分区: {partitionName}", Color.Blue);
            }
            else
            {
                AppendLog($"未找到分区: {partitionName}", Color.Orange);
            }
        }

        private void button16_Click(object sender, EventArgs e)
        {

        }

        private void checkbox19_CheckedChanged(object sender, BoolEventArgs e)
        {

        }
        
        #endregion

        #region Payload 操作

        private async void 提取PayloadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Payload Files (payload.bin, *.zip)|payload.bin;*.zip|All files (*.*)|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.Title = "Select payload.bin or zip file";
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string? outputDir = SelectDirectoryWithFileDialog("Select Output Directory");
                    if (string.IsNullOrWhiteSpace(outputDir))
                    {
                        AppendLog("已取消输出目录选择", Color.Gray);
                        return;
                    }

                    string inputFile = openFileDialog.FileName;
                    string outputDirPath = outputDir!;
                    string tempDir = Path.Combine(Path.GetTempPath(), "PayloadDumper_" + Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempDir);

                    await Task.Run(() =>
                    {
                        try
                        {
                            AppendLog($"Starting extraction of {inputFile}...", Color.Black);
                            using (var payload = new OPFlashTool.FastbootEnhance.Payload(inputFile, tempDir))
                            {
                                payload.init();
                                AppendLog($"Payload initialized. Format version: {payload.file_format_version}", Color.Blue);

                                foreach (var partition in payload.manifest.Partitions)
                                {
                                    AppendLog($"Extracting {partition.PartitionName}...", Color.Black);
                                    try
                                    {
                                        payload.extract(partition.PartitionName, outputDirPath, false, false);
                                        AppendLog($"Extracted {partition.PartitionName}", Color.Green);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppendLog($"Failed to extract {partition.PartitionName}: {ex.Message}", Color.Red);
                                    }
                                }
                            }
                            AppendLog("Extraction complete.", Color.Green);
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"Error during extraction: {ex.Message}", Color.Red);
                        }
                        finally
                        {
                            if (Directory.Exists(tempDir))
                            {
                                try { Directory.Delete(tempDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Cleanup] {ex.Message}"); }
                            }
                        }
                    });
                }
            }
        }

        private async void 合并SuperToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            string? rootDir = SelectDirectoryWithFileDialog("请选择固件根目录 (包含 META 和 IMAGES 文件夹)");
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                AppendLog("已取消选择固件目录", Color.Gray);
                return;
            }

            string rootDirPath = rootDir!;
            string metaDir = Path.Combine(rootDirPath, "META");

            // 1. 查找 JSON 配置文件
            string? jsonPath = null;

            // 优先在 META 目录下查找
            if (Directory.Exists(metaDir))
            {
                var jsonFiles = Directory.GetFiles(metaDir, "*.json");
                // 优先找 super_def*.json，否则取第一个 json
                jsonPath = jsonFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("super_def", StringComparison.OrdinalIgnoreCase))
                           ?? jsonFiles.FirstOrDefault();
            }

            // 如果 META 下没找到，尝试在根目录下查找
            if (jsonPath == null)
            {
                var jsonFiles = Directory.GetFiles(rootDirPath, "*.json");
                jsonPath = jsonFiles.FirstOrDefault(f => Path.GetFileName(f).StartsWith("super_def", StringComparison.OrdinalIgnoreCase))
                           ?? jsonFiles.FirstOrDefault();
            }

            if (jsonPath == null)
            {
                AppendLog("错误: 未找到 super_def 配置文件 (JSON)", Color.Red);
                ShowErrorMessage("在所选目录及其 META 子目录中未找到 JSON 配置文件！");
                return;
            }

            // 默认输出到 IMAGES，否则回退到根目录下的 super_output
            string outputDir = Path.Combine(rootDirPath, "IMAGES");
            if (!Directory.Exists(outputDir))
            {
                outputDir = Path.Combine(rootDirPath, "super_output");
            }
            Directory.CreateDirectory(outputDir);

            AppendLog($"[Super] 选中根目录: {rootDirPath}", Color.Black);
            AppendLog($"[Super] 找到配置文件: {Path.GetFileName(jsonPath)}", Color.Blue);

            await Task.Run(async () =>
            {
                var maker = new SuperMaker(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
                // 关键: 传入 rootDir 作为 imageRootDir，这样 SuperMaker 就能正确解析 IMAGES/xxx.img
                bool success = await maker.MakeSuperImgAsync(jsonPath, outputDir, rootDirPath);
                AppendLog(success ? "Super 生成成功" : "Super 生成失败", success ? Color.Green : Color.Red);
            });
        }

        private void input18_TextChanged(object sender, EventArgs e)
        {

        }

        private async void 激活LUNToolStripMenuItem_Click_1(object sender, EventArgs e)
        {
            if (!TryGetSerialForAction("激活 LUN", out string port)) return;

            _cts = new CancellationTokenSource();
            var flasher = new AutoFlasher(Application.StartupPath, (msg) => AppendLog(msg, Color.Black));
            AuthType authType = GetAuthType();
            var authFiles = await PrepareAuthFilesAsync();
            if (!authFiles.ok) return;
            var digest = authFiles.digest;
            var signature = authFiles.signature;

            // 使用 RunFlashActionAsync 来复用连接和配置逻辑
            var progPath = await EnsureProgrammerPathAsync();
            if (string.IsNullOrEmpty(progPath)) return;

            await flasher.RunFlashActionAsync(port, progPath!, authType, checkbox4.Checked, digest, signature, async (executor) =>
            {
                // 根据配置后的存储类型自动识别
                string type = executor.Client.StorageType; // "ufs" or "emmc"
                int targetLun = 0;

                if (type == "ufs")
                {
                    // UFS: 询问用户激活 LUN1 还是 LUN2
                    if (MessageBox.Show("检测到 UFS 存储。\n是否激活 LUN 1 (Boot A)?\n\n[是] = LUN 1\n[否] = LUN 2", "选择启动 LUN", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        targetLun = 1;
                    }
                    else
                    {
                        targetLun = 2;
                    }
                }
                else
                {
                    // EMMC: 默认 LUN 0
                    targetLun = 0;
                }

                AppendLog($"[激活] 识别到存储类型: {type.ToUpper()} -> 目标 LUN: {targetLun}", Color.Blue);

                bool success = executor.Client.SetBootLun(targetLun);
                if (success)
                {
                    AppendLog($"激活 LUN{targetLun} 成功", Color.Green);
                }
                else
                {
                    AppendLog($"激活 LUN{targetLun} 失败", Color.Red);
                }
            }, _cts.Token);
        }

        private void eDL重启到系统ToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        #region 进度条和状态更新

        private DateTime _lastUiUpdate = DateTime.MinValue;
        private Stopwatch? _operationStopwatch;

        /// <summary>
        /// 重置进度条和状态
        /// </summary>
        private void ResetProgress()
        {
            if (InvokeRequired) { Invoke(new Action(ResetProgress)); return; }
            progress1.Value = 0f;
            progress2.Value = 0f;
            label2.Text = "速度：0KB/s";
            label3.Text = "时间：00:00";
            input8.Text = "状态：等待操作...";
        }

        /// <summary>
        /// 更新进度条、速度和时间
        /// </summary>
        private void UpdateProgress(long current, long total, Stopwatch? sw = null)
        {
            // 限制 UI 更新频率 (100ms)
            if (current < total && (DateTime.Now - _lastUiUpdate).TotalMilliseconds < 100)
            {
                return;
            }
            _lastUiUpdate = DateTime.Now;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => PerformUpdateUi(current, total, sw)));
            }
            else
            {
                PerformUpdateUi(current, total, sw);
            }
        }

        private void PerformUpdateUi(long current, long total, Stopwatch? sw)
        {
            try
            {
                // 计算百分比 (AntdUI Progress 使用 float 0-1)
                float percent = 0;
                if (total > 0) percent = (float)current / total;
                if (percent > 1) percent = 1;
                if (percent < 0) percent = 0;

                // 更新进度条 (AntdUI Progress Value 是 0-1 的 float)
                progress1.Value = percent;
                progress2.Value = percent; // 同时更新第二个进度条

                // 更新速度
                if (sw != null)
                {
                    double elapsedSeconds = sw.Elapsed.TotalSeconds;
                    if (elapsedSeconds > 0.1)
                    {
                        double speedBytes = current / elapsedSeconds;
                        double speedKb = speedBytes / 1024.0;
                        double speedMb = speedBytes / 1024.0 / 1024.0;

                        if (speedMb >= 1)
                            label2.Text = $"速度：{speedMb:F1}MB/s";
                        else
                            label2.Text = $"速度：{speedKb:F0}KB/s";

                        // 更新时间
                        TimeSpan elapsed = sw.Elapsed;
                        label3.Text = $"时间：{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
                    }
                }

                // 更新状态 (percent 是 0-1 的 float，需要转换为百分比显示)
                int percentDisplay = (int)(percent * 100);
                if (percentDisplay >= 100)
                {
                    input8.Text = "状态：操作完成";
                }
                else if (percentDisplay > 0)
                {
                    string currentStr = (current >= 1024 * 1024 * 1024)
                        ? $"{(current / 1024.0 / 1024.0 / 1024.0):F2} GB"
                        : $"{(current / 1024.0 / 1024.0):F0} MB";
                    input8.Text = $"状态：{percentDisplay}% ({currentStr})";
                }
            }
            catch { }
        }

        /// <summary>
        /// 更新进度 (无计时器版本)
        /// </summary>
        private void UpdateProgress(long current, long total)
        {
            UpdateProgress(current, total, null);
        }

        #endregion

        #endregion

        private void checkbox2_CheckedChanged(object sender, BoolEventArgs e)
        {

        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {

        }
    }
}
