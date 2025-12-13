using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using OPFlashTool.Authentication;
using OPFlashTool.Strategies;
using OPFlashTool.Services;

namespace OPFlashTool.Qualcomm
{
    // 定义认证类型枚举，方便 UI 传参
    public enum AuthType
    {
        Standard, // 标准 (无验证)
        Vip,      // Oppo/Realme VIP
        Xiaomi    // 小米免授权
    }

    public class AutoFlasher
    {
        private Action<string> _log;
        private string _baseDir;    // bin 目录
        private string _loaderDir;  // bin/Loaders 目录

        public AutoFlasher(string binDirectory, Action<string> logCallback)
        {
            _baseDir = binDirectory;
            _loaderDir = Path.Combine(binDirectory, "Loaders");
            
            // 自动创建目录，方便用户放文件
            if (!Directory.Exists(_loaderDir)) Directory.CreateDirectory(_loaderDir);
            
            _log = logCallback;
        }

        // 工厂方法：根据枚举获取策略
        private IDeviceStrategy GetDeviceStrategy(AuthType type)
        {
            switch (type)
            {
                case AuthType.Vip: return new OppoVipDeviceStrategy();
                case AuthType.Xiaomi: return new XiaomiDeviceStrategy();
                case AuthType.Standard: 
                default: return new StandardDeviceStrategy();
            }
        }

        /// <summary>
        /// 智能刷机主流程 (带回调)
        /// </summary>
        public async Task<bool> RunFlashActionAsync(
            string portName, 
            string userProgPath, 
            AuthType authType, 
            bool skipLoader,
            string userDigestPath, 
            string userSignPath,
            Func<FlashTaskExecutor, Task> flashAction,
            CancellationToken ct = default,
            Func<string, string> inputRequestCallback = null,
            string preferredStorageType = "Auto")
        {
            // 1. 获取策略对象
            IDeviceStrategy strategy = GetDeviceStrategy(authType);

            if (string.IsNullOrEmpty(portName))
            {
                _log("错误: 端口名不能为空");
                return false;
            }

            return await Task.Run(async () =>
            {
                SerialPort port = null;
                CancellationTokenRegistration ctr = default;

                try
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException();

                    // [优化] 显式创建，便于在 finally 中控制
                    port = new SerialPort(portName, 115200);
                    port.ReadTimeout = 5000;
                    port.WriteTimeout = 5000;
                    
                    // [热插拔优化] 重试打开端口，处理突然断开后的重连
                    int openRetries = 3;
                    while (openRetries-- > 0)
                    {
                        try 
                        {
                            port.Open();
                            break; // 成功打开
                        }
                        catch (UnauthorizedAccessException) when (openRetries > 0)
                        {
                            // 端口被占用，可能是之前的连接未完全释放
                            _log($"[重试] 端口 {portName} 被占用，等待释放... ({3 - openRetries}/3)");
                            
                            // 强制 GC 回收可能残留的 SerialPort 对象
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            
                            Thread.Sleep(1000); // 等待系统释放端口
                        }
                        catch (Exception ex)
                        {
                            _log($"[错误] 无法打开端口 {portName}: {ex.Message}");
                            return false;
                        }
                    }
                    
                    if (!port.IsOpen)
                    {
                        _log($"[错误] 重试后仍无法打开端口 {portName}");
                        return false;
                    }

                    _log($"[连接] 打开端口 {portName} | 认证模式: {strategy.Name}");

                    // [新增] 注册取消回调，强制关闭端口以中断阻塞操作
                    ctr = ct.Register(() => 
                    {
                        try 
                        { 
                            if (port != null && port.IsOpen) 
                            {
                                _log("[停止] 用户强制停止，正在关闭端口...");
                                port.Close(); 
                            }
                        } 
                        catch {}
                    });

                    if (ct.IsCancellationRequested) throw new OperationCanceledException();

                    uint? detectedHwId = null;

                    // --- START 核心逻辑 ---
                    // 1. Sahara 引导
                    if (!skipLoader)
                    {
                        var sahara = new SaharaClient(port, _log);
                        string finalLoader = userProgPath;
                        
                        // A. 策略：用户没选文件 -> 尝试自动从设备读取 ID 并查找
                        if (string.IsNullOrEmpty(finalLoader) || !File.Exists(finalLoader))
                        {
                            _log("[自动] 未指定引导文件，尝试读取设备 ID...");
                            
                            // 尝试无文件读取 ID
                            var devInfo = sahara.ReadDeviceInfo();

                            if (devInfo.ContainsKey("MSM_HWID"))
                            {
                                string hwIdStr = devInfo["MSM_HWID"];
                                // 取前 8 个字符 (4字节) 作为 HWID
                                if (hwIdStr.Length >= 8)
                                {
                                    try 
                                    {
                                        detectedHwId = Convert.ToUInt32(hwIdStr.Substring(0, 8), 16);
                                        string chipName = QualcommDatabase.GetChipName(detectedHwId.Value);
                                        _log($"[识别] 芯片型号: {chipName} (HWID: {detectedHwId.Value:X})");
                                        
                                        if (devInfo.ContainsKey("PK_HASH")) _log($"[识别] PK Hash: {devInfo["PK_HASH"]}");
                                        if (devInfo.ContainsKey("SerialNumber")) _log($"[识别] Serial: {devInfo["SerialNumber"]}");

                                        // 自动查找: bin/Loaders/prog_firehose_{chipName}.{ext}
                                        string[] extensions = { ".elf", ".melf", ".mbn", ".bin", ".digest" };
                                        string foundPath = null;

                                        // 1. 尝试匹配专用引导 (prog_firehose_{chipName})
                                        foreach (var ext in extensions)
                                        {
                                            string p = Path.Combine(_loaderDir, $"prog_firehose_{chipName.ToLower()}{ext}");
                                            if (File.Exists(p))
                                            {
                                                foundPath = p;
                                                break;
                                            }
                                        }

                                        // 2. 尝试匹配通用引导 (prog_firehose_ddr)
                                        if (foundPath == null)
                                        {
                                            foreach (var ext in extensions)
                                            {
                                                string p = Path.Combine(_loaderDir, $"prog_firehose_ddr{ext}");
                                                if (File.Exists(p))
                                                {
                                                    foundPath = p;
                                                    break;
                                                }
                                            }
                                        }

                                        if (foundPath != null)
                                        {
                                            finalLoader = foundPath;
                                            _log($"[自动] 已匹配引导文件: {Path.GetFileName(finalLoader)}");
                                        }
                                        else
                                        {
                                            _log($"[错误] 数据库匹配到 {chipName}，但目录中缺少对应的引导文件 (.elf/.melf/.mbn/.bin/.digest)！");
                                            return false;
                                        }
                                    }
                                    catch { _log("[错误] 解析 HWID 失败"); }
                                }
                            }
                            else
                            {
                                _log("[错误] 无法读取设备 ID，且未手动指定引导文件。");
                                return false;
                            }
                        }
                        else
                        {
                            _log($"[手动] 使用用户指定的引导文件: {Path.GetFileName(finalLoader)}");
                        }
                        
                        if (!sahara.HandshakeAndLoad(finalLoader))
                        {
                             _log("[失败] Sahara 引导失败，请检查文件是否匹配。");
                             return false; 
                        }
                        
                        _log("[等待] Firehose 正在启动...");
                        Thread.Sleep(1500);
                    }
                    else
                    {
                         _log("[引导] 跳过引导阶段 (假设设备已在 Firehose 模式)");
                    }

                    // 2. 创建 Client
                    var firehose = new FirehoseClient(port, _log);

                    // 3. 策略认证
                    bool authResult = true;

                    // [根本性修复] 如果跳过了 Loader，说明我们正在复用一个已经建立的会话。
                    // 此时设备通常已经处于 Authenticated 状态。
                    // 如果再次发送认证指令（如小米的 <sig>），设备会报错 Failed to run the last command。
                    // 因此，当 skipLoader=true 时，我们应当跳过认证步骤。
                    if (!skipLoader)
                    {
                        authResult = await strategy.AuthenticateAsync(
                            firehose, 
                            userProgPath ?? "", 
                            _log, 
                            inputRequestCallback, 
                            userDigestPath, 
                            userSignPath
                        );
                    }
                    else
                    {
                        _log("[认证] 复用会话模式：跳过重复认证步骤");
                    }

                    if (!authResult)
                    {
                        _log($"[错误] {strategy.Name} 认证失败！中止操作。");
                        return false;
                    }

                    // 4. 配置 UFS/EMMC (智能配置)
                    string storageType = "ufs"; // 默认 UFS
                    bool isAuto = string.Equals(preferredStorageType, "Auto", StringComparison.OrdinalIgnoreCase);

                    if (!isAuto && !string.IsNullOrEmpty(preferredStorageType))
                    {
                        storageType = preferredStorageType.ToLower();
                        _log($"[配置] 强制使用存储类型: {storageType.ToUpper()}");
                    }
                    else if (detectedHwId != null)
                    {
                        // 自动识别逻辑
                        string chipNameForConfig = QualcommDatabase.GetChipName(detectedHwId.Value);
                        var memType = QualcommDatabase.GetMemoryType(chipNameForConfig);
                        
                        storageType = (memType == MemoryType.Emmc) ? "emmc" : "ufs";
                        _log($"[智能] 芯片 {chipNameForConfig} -> 推荐配置: {storageType.ToUpper()}");
                    }

                    // 针对高端芯片的特殊优化：强制 UFS，不重试 EMMC (仅在自动模式下)
                    bool isHighEndChip = isAuto && detectedHwId != null && 
                                        (QualcommDatabase.GetChipName(detectedHwId.Value).StartsWith("SM8") || 
                                         QualcommDatabase.GetChipName(detectedHwId.Value).Contains("Snapdragon 8"));

                    if (isHighEndChip)
                    {
                        // 高端旗舰芯片，强制 UFS
                        if (!firehose.Configure("ufs")) 
                        {
                            _log($"[错误] UFS 配置失败 (芯片不支持 EMMC 降级重试)");
                            return false;
                        }
                    }
                    else
                    {
                        // 普通芯片或强制模式
                        if (!firehose.Configure(storageType))
                        {
                            if (isAuto)
                            {
                                _log($"[重试] 配置 {storageType} 失败，尝试切换模式...");
                                string retryType = (storageType == "ufs") ? "emmc" : "ufs";
                                if (!firehose.Configure(retryType))
                                {
                                    _log("[失败] 存储配置完全失败。");
                                    return false;
                                }
                                storageType = retryType;
                            }
                            else
                            {
                                _log($"[失败] 强制配置 {storageType} 失败。");
                                return false;
                            }
                        }
                    }

                    _log($"[就绪] 设备连接成功 ({storageType.ToUpper()})");

                    // [新增] 激活 LUN 逻辑 (EMMC=0, UFS=1)
                    // 注意：这通常只在需要时调用，但如果用户要求“激活LUN...根据UI选择”，
                    // 这里我们只是配置了 Firehose。具体的 SetBootLun 应该由上层 flashAction 决定是否调用，
                    // 或者我们在这里默认设置？
                    // 用户说 "激活lun分为EMMC激活0ufs激活默认1...配置设备也是如此"
                    // 这可能意味着 Configure 之后应该 SetBootLun。
                    // 但 SetBootLun 是修改启动分区，可能会变砖。
                    // 只有在 "激活LUN" 操作时才应该调用。
                    // 所以这里不自动调用 SetBootLun。

                    // 5. 执行任务
                    if (flashAction != null)
                    {
                        // 创建执行器
                        var executor = new FlashTaskExecutor(firehose, strategy, _log, firehose.SectorSize);
                        
                        // [核心修复] 正确挂载进度事件
                        // 将底层 executor 的事件转发给 UI 传进来的 progressCallback
                        // 注意：FlashTaskExecutor 现在直接使用 context 更新 UI，这里不需要再转发了
                        // 但为了兼容性，如果 progressCallback 仍然被使用（例如非 CloudContext 场景），可以保留
                        // 不过我们已经修改了 RunFlashActionAsync 签名，progressCallback 已经移除了

                        // 执行具体的刷写逻辑
                        await flashAction(executor);
                    }
                    // --- END 核心逻辑 ---

                    return true;
                }
                catch (OperationCanceledException)
                {
                    _log("[信息] 操作已取消，正在断开连接...");
                    return false;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        _log("[信息] 操作已强制终止");
                        return false;
                    }
                    _log($"[异常] {ex.Message}");
                    return false;
                }
                finally
                {
                    ctr.Dispose();

                    // [热插拔优化] 强制彻底释放端口资源
                    ForceReleasePort(ref port, portName, _log);
                }
            }, ct);
        }

        /// <summary>
        /// 强制释放端口资源
        /// </summary>
        private void ForceReleasePort(ref SerialPort port, string portName, Action<string> logCallback)
        {
            if (port != null)
            {
                if (port.IsOpen)
                {
                    try 
                    {
                        // 尝试清理缓冲区，虽然可能抛异常
                        port.DiscardInBuffer();
                        port.DiscardOutBuffer();
                    } catch {}

                    try
                    {
                        port.Close(); // 关闭连接
                    }
                    catch (Exception ex)
                    {
                        logCallback($"[警告] 关闭端口时出错: {ex.Message}");
                    }
                }
                
                port.Dispose(); // 释放资源
                port = null;    // 解除引用
                
                logCallback($"[断开] 端口 {portName} 已释放");
            }
        }

        /// <summary>
        /// 智能刷机主流程 (旧版兼容)
        /// </summary>
        public async Task<bool> RunAutoProcess(
            string portName, 
            string userProgPath, 
            bool enableVip, 
            string userDigestPath, 
            string userSignPath)
        {
            AuthType auth = enableVip ? AuthType.Vip : AuthType.Standard;
            return await RunFlashActionAsync(portName, userProgPath, auth, false, userDigestPath, userSignPath, null);
        }
    }
}