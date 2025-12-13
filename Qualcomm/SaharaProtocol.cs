using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace OPFlashTool.Qualcomm
{
    // --- 1. 协议枚举定义 ---
    public enum SaharaCommandId : uint
    {
        Hello = 0x01,
        HelloResp = 0x02,
        ReadData = 0x03,        // 32位读取 (老设备)
        EndImageTx = 0x04,
        Done = 0x05,
        DoneResp = 0x06,
        Reset = 0x07,
        MemoryDebug = 0x09,
        MemoryRead = 0x0A,
        CmdReady = 0x0B,        // 命令模式就绪
        CmdSwitchMode = 0x0C,   // 切换模式
        CmdExec = 0x0D,         // 执行命令
        CmdExecResp = 0x0E,     // 命令响应
        CmdExecData = 0x0F,     // 命令数据传输
        ReadData64 = 0x12       // [关键] 64位读取 (新设备)
    }

    public enum SaharaMode : uint
    {
        ImageTxPending = 0x0,   // 准备上传引导
        ImageTxComplete = 0x1,
        MemoryDebug = 0x2,
        Command = 0x3           // 命令模式 (读取信息)
    }

    public enum SaharaExecCmdId : uint
    {
        SerialNumRead = 0x01,
        MsmHwIdRead = 0x02,
        OemPkHashRead = 0x03
    }

    public enum SaharaStatus : uint
    {
        Success = 0x00,
        NakInvalidCmd = 0x01,
        NakAuthFail = 0x0B
    }

    // --- 2. 数据包结构体 (按字节对齐) ---
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SaharaHeader
    {
        public uint CommandId;
        public uint Length;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SaharaHelloResp
    {
        public SaharaHeader Header;   // 8 bytes
        public uint Version;          // 协议版本 (通常设为 2)
        public uint VersionSupported; // 支持的最低版本 (通常设为 1)
        public uint Status;           // 状态 (0 = Success)
        public uint Mode;             // 目标模式
        // [重要] 新设备(SM8450+)如果检测到 Reserved 字段有脏数据会拒绝连接
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
        public uint Reserved4;
        public uint Reserved5;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SaharaSwitchMode
    {
        public SaharaHeader Header;
        public uint Mode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SaharaCmdExec
    {
        public SaharaHeader Header;
        public uint ClientCommand;
    }

    // --- 3. 核心通信类 ---
    public class SaharaClient
    {
        private SerialPort _port;
        private Action<string> _log;
        
        // 缓冲区设置，适配 USB 包大小
        private const int MAX_BUFFER_SIZE = 4096;

        public SaharaClient(SerialPort port, Action<string> logger = null)
        {
            _port = port;
            _log = logger ?? Console.WriteLine;
        }

        private void UpdateProgress(long current, long total, Stopwatch sw = null)
        {
            // 云端功能已移除 - 进度由日志输出
        }

        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F1} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            return $"{bytesPerSec / 1024 / 1024:F1} MB/s";
        }

        // ==========================================
        // 功能 1: 握手并上传引导程序 (Firehose Programmer)
        // ==========================================
        public bool HandshakeAndLoad(string programmerPath)
        {
            if (!File.Exists(programmerPath))
            {
                _log($"[Error] 引导文件不存在: {programmerPath}");
                return false;
            }

            byte[] fileBytes = File.ReadAllBytes(programmerPath);
            // Keep Sahara stage quiet; UI already shows progress

            Stopwatch sw = new Stopwatch();
            sw.Start();

            try
            {
                bool done = false;
                bool endImageTxSeen = false;
                int endImageTxRepeat = 0;
                int loopGuard = 0;

                while (!done)
                {
                    if (loopGuard++ > 512)
                    {
                        _log("[Error] Sahara 循环超限");
                        return false;
                    }

                    // 1. 读取包头 (8字节)
                    byte[] headBuf = ReadBytes(8);
                    uint cmdId = BitConverter.ToUInt32(headBuf, 0);
                    uint pktLen = BitConverter.ToUInt32(headBuf, 4);

                    // 2. 根据指令处理
                    switch ((SaharaCommandId)cmdId)
                    {
                        case SaharaCommandId.Hello:
                            // 读完剩余的 Hello 包体 (48 - 8 = 40 bytes)
                            byte[] helloBody = ReadBytes((int)pktLen - 8);
                            uint devVer = BitConverter.ToUInt32(helloBody, 0); // 设备协议版本
                            uint devMode = BitConverter.ToUInt32(helloBody, 12); // 设备当前模式
                            
                            // 始终响应 ImageTxPending 模式，准备上传
                            // Version=2 兼容性最好
                            SendHelloResponse(SaharaMode.ImageTxPending, 2); 
                            break;

                        case SaharaCommandId.ReadData:
                            // [兼容老设备] 32位地址请求
                            if (pktLen < 20) throw new Exception("ReadData 包长度错误");
                            byte[] body = ReadBytes((int)pktLen - 8);
                            uint imageId = BitConverter.ToUInt32(body, 0);
                            uint offset = BitConverter.ToUInt32(body, 4);
                            uint length = BitConverter.ToUInt32(body, 8);

                            if (!UploadDataBlock(fileBytes, offset, length, sw)) return false;
                            break;

                        case SaharaCommandId.ReadData64:
                            // [兼容新设备] 64位地址请求 (SM8450+ 常见)
                            if (pktLen < 32) throw new Exception("ReadData64 包长度错误");
                            byte[] body64 = ReadBytes((int)pktLen - 8);
                            ulong imageId64 = BitConverter.ToUInt64(body64, 0);
                            ulong offset64 = BitConverter.ToUInt64(body64, 8);
                            ulong length64 = BitConverter.ToUInt64(body64, 16);

                            if (!UploadDataBlock(fileBytes, (long)offset64, (long)length64, sw)) return false;
                            break;

                        case SaharaCommandId.EndImageTx:
                            ReadBytes((int)pktLen - 8); // 读完剩余部分
                            // 检查状态 (可选)
                            // uint status = BitConverter.ToUInt32(body, 0);

                            if (!endImageTxSeen)
                            {
                                SendDone(); // 发送 Done 确认
                                endImageTxSeen = true;
                            }
                            else
                            {
                                if (endImageTxRepeat++ == 0) { /* mute duplicate notice */ }
                            }
                            break;

                        case SaharaCommandId.DoneResp:
                            ReadBytes((int)pktLen - 8);
                            _log("[Sahara] 引导加载完成");
                            done = true; // 流程结束
                            break;

                        case SaharaCommandId.Reset:
                            _log("[Error] Sahara 设备复位请求");
                            return false;

                        default:
                            _log($"[Sahara] 忽略未知指令: 0x{cmdId:X} Len:{pktLen}");
                            // 消费掉未知包的数据，防止流错位
                            if (pktLen > 8) ReadBytes((int)pktLen - 8);
                            break;
                    }
                }
                sw.Stop();
                UpdateProgress(fileBytes.Length, fileBytes.Length); // 100%
                return true;
            }
            catch (Exception ex)
            {
                _log($"[Sahara] 通信异常: {ex.Message}");
                return false;
            }
        }

        // ==========================================
        // 功能 2: 读取设备信息 (无需引导文件)
        // 注意：Sahara V3 (SM8450+) 设备不支持命令模式读取 PK Hash
        // ==========================================
        public Dictionary<string, string> ReadDeviceInfo()
        {
            var info = new Dictionary<string, string>();
            try
            {
                // 1. 等待并探测 Hello 包
                if (!WaitForHello(out uint version)) return info;
                
                // 记录 Sahara 版本
                info["SaharaVersion"] = version.ToString();
                _log($"[Sahara] 检测到协议版本: V{version}");

                // 2. 尝试进入命令模式 (Command Mode)
                if (!EnterCommandMode(version))
                {
                    // V3 设备通常拒绝命令模式
                    if (version >= 3)
                    {
                        _log("[Sahara V3] 设备拒绝命令模式，这是 V3 协议的预期行为。");
                        _log("[Sahara V3] 请手动指定引导文件 (loader)，自动检测不可用。");
                        info["IsV3"] = "true";
                    }
                    else
                    {
                        _log("[Info] 设备拒绝命令模式，尝试复位。");
                    }
                    SendReset();
                    return info;
                }

                // 3. 执行读取指令
                uint? serial = ExecuteReadSerial();
                if (serial != null) info["SerialNumber"] = serial.Value.ToString("X8");

                byte[] hwid = ExecuteReadHwId();
                if (hwid != null) 
                {
                    string hwIdStr = BitConverter.ToString(hwid).Replace("-", "");
                    info["MSM_HWID"] = hwIdStr;
                    
                    // 尝试识别芯片并检测是否为 V3
                    if (hwIdStr.Length >= 8)
                    {
                        try
                        {
                            uint hwId = Convert.ToUInt32(hwIdStr.Substring(0, 8), 16);
                            string chipName = QualcommDatabase.GetChipName(hwId);
                            info["ChipName"] = chipName;
                            
                            int saharaVer = QualcommDatabase.GetSaharaVersion(chipName);
                            if (saharaVer >= 3)
                            {
                                _log($"[Sahara] 芯片 {chipName} 使用 V{saharaVer} 协议");
                                info["IsV3"] = "true";
                            }
                        }
                        catch { /* 解析失败，忽略 */ }
                    }
                }

                byte[] pkhash = ExecuteReadPkHash();
                if (pkhash != null) 
                {
                    info["PK_HASH"] = BitConverter.ToString(pkhash).Replace("-", "").ToLower();
                }
                else if (version >= 3)
                {
                    _log("[Sahara V3] 无法读取 PK Hash，V3 设备不支持此功能。");
                }

                // 4. 读取完毕，切回 ImageTxPending 模式以便后续刷机
                SwitchMode(SaharaMode.ImageTxPending);
            }
            catch (Exception ex)
            {
                _log($"[Error] 读取信息失败: {ex.Message}");
            }
            return info;
        }

        // --- 核心辅助方法 ---

        private bool WaitForHello(out uint version)
        {
            version = 2;
            int oldTimeout = _port.ReadTimeout;
            _port.ReadTimeout = 2000; // 2秒探测超时

            try
            {
                byte[] head = new byte[8];
                // 尝试多次读取，清除缓冲区垃圾
                for(int i = 0; i < 5; i++)
                {
                    try 
                    {
                        int read = _port.Read(head, 0, 8);
                        if (read < 8) continue;

                        uint cmd = BitConverter.ToUInt32(head, 0);
                        uint len = BitConverter.ToUInt32(head, 4);

                        if (cmd == (uint)SaharaCommandId.Hello)
                        {
                            byte[] body = ReadBytes((int)len - 8);
                            version = BitConverter.ToUInt32(body, 0);
                            _log("[Sahara] 检测到设备 (Hello)");
                            _port.ReadTimeout = oldTimeout;
                            return true;
                        }
                        else
                        {
                            if (len > 8) ReadBytes((int)len - 8); // 读掉无效包
                        }
                    }
                    catch (TimeoutException) { }
                }
            }
            finally
            {
                _port.ReadTimeout = oldTimeout;
            }
            return false;
        }

        private bool EnterCommandMode(uint version)
        {
            // 请求进入 Command 模式
            SendHelloResponse(SaharaMode.Command, version);

            // 等待 CmdReady (0x0B)
            try
            {
                byte[] head = ReadBytes(8);
                uint cmd = BitConverter.ToUInt32(head, 0);
                uint len = BitConverter.ToUInt32(head, 4);
                ReadBytes((int)len - 8); // 读完 Body

                return cmd == (uint)SaharaCommandId.CmdReady;
            }
            catch 
            {
                return false;
            }
        }

        private void SendHelloResponse(SaharaMode mode, uint version)
        {
            var resp = new SaharaHelloResp
            {
                Header = new SaharaHeader { CommandId = (uint)SaharaCommandId.HelloResp, Length = 48 },
                Version = version,
                VersionSupported = 1,
                Status = (uint)SaharaStatus.Success,
                Mode = (uint)mode,
                // [关键] 必须显式清零 Reserved 字段
                Reserved0 = 0, Reserved1 = 0, Reserved2 = 0, 
                Reserved3 = 0, Reserved4 = 0, Reserved5 = 0
            };
            WriteStruct(resp);
        }

        private bool UploadDataBlock(byte[] fileData, long offset, long length, Stopwatch sw = null)
        {
            if (offset + length > fileData.Length)
            {
                _log($"[Error] 请求越界: Offset {offset} + Len {length} > FileSize {fileData.Length}");
                return false;
            }
            _port.Write(fileData, (int)offset, (int)length);
            
            // 更新进度
            UpdateProgress(offset + length, fileData.Length, sw);
            
            return true;
        }

        private void SendDone()
        {
            var done = new SaharaHeader { CommandId = (uint)SaharaCommandId.Done, Length = 8 };
            WriteStruct(done);
        }

        private void SendReset()
        {
            var reset = new SaharaHeader { CommandId = (uint)SaharaCommandId.Reset, Length = 8 };
            WriteStruct(reset);
        }

        private void SwitchMode(SaharaMode mode)
        {
            var cmd = new SaharaSwitchMode
            {
                Header = new SaharaHeader { CommandId = (uint)SaharaCommandId.CmdSwitchMode, Length = 12 },
                Mode = (uint)mode
            };
            WriteStruct(cmd);
        }

        // --- 命令执行封装 ---
        private uint? ExecuteReadSerial()
        {
            byte[] data = ExecuteCommand(SaharaExecCmdId.SerialNumRead);
            if (data != null && data.Length >= 4) return BitConverter.ToUInt32(data, 0);
            return null;
        }

        private byte[] ExecuteReadHwId() => ExecuteCommand(SaharaExecCmdId.MsmHwIdRead);
        private byte[] ExecuteReadPkHash() => ExecuteCommand(SaharaExecCmdId.OemPkHashRead);

        private byte[] ExecuteCommand(SaharaExecCmdId clientCmd)
        {
            var exec = new SaharaCmdExec
            {
                Header = new SaharaHeader { CommandId = (uint)SaharaCommandId.CmdExec, Length = 12 },
                ClientCommand = (uint)clientCmd
            };
            WriteStruct(exec);

            // 读取响应头
            byte[] head = ReadBytes(8);
            uint cmd = BitConverter.ToUInt32(head, 0);
            uint len = BitConverter.ToUInt32(head, 4);

            if (cmd == (uint)SaharaCommandId.CmdExecResp)
            {
                byte[] body = ReadBytes((int)len - 8);
                uint respLen = BitConverter.ToUInt32(body, 4); // 响应数据长度

                // 再次请求具体数据 (CmdExecData)
                var execData = new SaharaCmdExec
                {
                    Header = new SaharaHeader { CommandId = (uint)SaharaCommandId.CmdExecData, Length = 12 },
                    ClientCommand = (uint)clientCmd
                };
                WriteStruct(execData);

                return ReadBytes((int)respLen);
            }
            
            // 如果出错或非响应，读完剩余字节
            if (len > 8) ReadBytes((int)len - 8);
            return null;
        }

        // --- 底层 IO 方法 ---
        private byte[] ReadBytes(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            int retries = 0;
            
            while (offset < count)
            {
                int read = _port.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    if(retries++ > 3) throw new TimeoutException("Sahara 读取超时");
                    Thread.Sleep(50);
                    continue;
                }
                offset += read;
            }
            return buffer;
        }

        private void WriteStruct<T>(T structure) where T : struct
        {
            int size = Marshal.SizeOf(structure);
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try {
                Marshal.StructureToPtr(structure, ptr, true);
                Marshal.Copy(ptr, arr, 0, size);
            } finally {
                Marshal.FreeHGlobal(ptr);
            }
            _port.Write(arr, 0, size);
        }
    }
}
