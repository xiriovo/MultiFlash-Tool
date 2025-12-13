# MultiFlash Tool 快速参考指南

快速查找常用命令和操作的参考手册。

## 📑 目录

- [设备模式](#设备模式)
- [常用操作](#常用操作)
- [命令参数](#命令参数)
- [快捷键](#快捷键)
- [故障排除](#故障排除)

## 🔌 设备模式

### 进入 EDL 模式（9008）

**方法 1：通过 ADB**
```bash
adb reboot edl
```

**方法 2：通过 Fastboot**
```bash
fastboot oem edl
# 或
fastboot reboot emergency
```

**方法 3：硬件按键**
- 关机状态下同时按住 `音量+ + 音量- + 电源键`
- 具体按键组合因设备而异

### 进入 Fastboot 模式

**方法 1：通过 ADB**
```bash
adb reboot bootloader
```

**方法 2：硬件按键**
- 关机状态下按住 `音量- + 电源键`

### 进入 Recovery 模式

**通过 ADB**
```bash
adb reboot recovery
```

**通过 Fastboot**
```bash
fastboot reboot recovery
```

## ⚡ 常用操作

### EDL 模式操作

| 操作 | 说明 | 注意事项 |
|------|------|----------|
| **读取分区** | 备份指定分区到本地 | 需要足够的磁盘空间 |
| **写入分区** | 刷写分区镜像到设备 | 确认分区名称正确 |
| **擦除分区** | 清空指定分区数据 | 谨慎操作，不可恢复 |
| **备份 GPT** | 备份分区表 | 建议定期备份 |
| **恢复 GPT** | 恢复分区表 | 可修复分区表损坏 |
| **全量刷机** | 刷写完整固件包 | 耗时较长，保持连接 |

### Fastboot 模式操作

| 命令 | 说明 | 示例 |
|------|------|------|
| `fastboot devices` | 检测设备 | 确认设备连接 |
| `fastboot getvar all` | 获取设备信息 | 查看所有变量 |
| `fastboot flash <分区> <镜像>` | 刷写分区 | `fastboot flash boot boot.img` |
| `fastboot erase <分区>` | 擦除分区 | `fastboot erase userdata` |
| `fastboot reboot` | 重启设备 | 刷写后重启 |
| `fastboot oem unlock` | 解锁 Bootloader | 会清空数据 |
| `fastboot oem lock` | 锁定 Bootloader | 需要官方固件 |

### 固件工具操作

| 工具 | 功能 | 输入 | 输出 |
|------|------|------|------|
| **Payload 提取** | 从 OTA 包提取分区 | payload.bin | 分区镜像文件 |
| **Super 合并** | 合并动态分区 | 多个分区镜像 | super.img |
| **稀疏镜像转换** | 转换镜像格式 | .img.sparse | .img |

## 🎯 命令参数

### Programmer 文件

常见的 Programmer 文件类型：
- `prog_emmc_firehose_*.mbn` - eMMC 存储
- `prog_ufs_firehose_*.elf` - UFS 存储
- `rawprogram*.xml` - 分区配置
- `patch*.xml` - 补丁配置

### 分区名称

常见分区及其作用：

| 分区名 | 作用 | 可否刷写 |
|--------|------|----------|
| `boot` | 内核和 ramdisk | ✅ 可以 |
| `system` | 系统分区 | ✅ 可以 |
| `vendor` | 厂商分区 | ✅ 可以 |
| `userdata` | 用户数据 | ✅ 可以（会清空） |
| `recovery` | 恢复分区 | ✅ 可以 |
| `vbmeta` | 验证元数据 | ✅ 可以 |
| `dtbo` | 设备树 | ✅ 可以 |
| `modem` | 基带固件 | ⚠️ 谨慎 |
| `abl` | Bootloader | ⚠️ 谨慎 |
| `xbl` | 引导加载器 | ❌ 危险 |

## ⌨️ 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl + L` | 清空日志 |
| `Ctrl + S` | 保存日志 |
| `Ctrl + R` | 刷新设备列表 |
| `F5` | 重新加载配置 |
| `Esc` | 取消当前操作 |

## 🔧 故障排除

### 设备无法识别

**问题**: 设备连接后不显示

**解决方案**:
1. 检查驱动安装
   ```bash
   # 查看设备管理器中是否有未知设备
   ```
2. 更换 USB 端口（使用 USB 2.0）
3. 重新安装驱动
4. 尝试不同的 USB 线

### EDL 刷写失败

**问题**: 刷写过程中断或失败

**解决方案**:
1. 确认 Programmer 文件匹配设备
2. 检查固件包完整性（MD5/SHA256）
3. 确保 USB 连接稳定
4. 关闭杀毒软件
5. 以管理员身份运行

### Fastboot 命令无响应

**问题**: Fastboot 命令执行卡住

**解决方案**:
1. 确认设备在 Fastboot 模式
   ```bash
   fastboot devices
   ```
2. 重启到 Fastboot
   ```bash
   adb reboot bootloader
   ```
3. 更新 Fastboot 工具到最新版本

### 刷机后无法开机

**问题**: 刷写完成后设备卡在开机画面

**解决方案**:
1. 进入 Recovery 清除缓存
2. 恢复 GPT 分区表
3. 重新刷写完整固件
4. 检查是否刷错固件版本

## 📊 状态码说明

### EDL 模式状态码

| 代码 | 说明 | 处理方法 |
|------|------|----------|
| `0x00` | 成功 | 正常 |
| `0x01` | 通信错误 | 检查连接 |
| `0x02` | 文件错误 | 检查文件路径 |
| `0x03` | 分区错误 | 确认分区名称 |
| `0x04` | 验证失败 | 检查文件完整性 |

### Fastboot 返回码

| 返回 | 说明 |
|------|------|
| `OKAY` | 命令执行成功 |
| `FAIL` | 命令执行失败 |
| `DATA` | 数据传输中 |
| `INFO` | 信息输出 |

## 🔍 日志分析

### 关键日志标识

**成功标识**:
```
[SUCCESS] Operation completed
[INFO] Flash successful
```

**错误标识**:
```
[ERROR] Failed to...
[FAIL] Cannot...
[CRITICAL] ...
```

**警告标识**:
```
[WARNING] ...
[NOTICE] ...
```

## 💡 最佳实践

### 刷机前
1. ✅ 备份重要数据
2. ✅ 确认固件版本匹配
3. ✅ 检查电池电量 > 50%
4. ✅ 准备好救砖工具

### 刷机中
1. ✅ 保持 USB 连接稳定
2. ✅ 不要断电或拔线
3. ✅ 观察日志输出
4. ✅ 记录错误信息

### 刷机后
1. ✅ 首次开机等待时间较长
2. ✅ 清除缓存和数据（如需要）
3. ✅ 验证功能正常
4. ✅ 保存刷机日志

## 📞 获取帮助

遇到问题？按以下顺序寻求帮助：

1. 📖 查看 [常见问题](../README.md#常见问题)
2. 🔍 搜索 [已有 Issues](https://github.com/xiriovo/MultiFlash-Tool/issues)
3. 💬 加入 [QQ 群](https://qm.qq.com/q/oCwGmTm5a2) 咨询
4. 📱 访问 [Telegram](https://t.me/OPFlashTool) 交流
5. 🐛 提交 [Bug 报告](https://github.com/xiriovo/MultiFlash-Tool/issues/new?template=bug_report.md)

---

<div align="center">
  
  **快速参考，随时查阅** 📚
  
  [返回主文档](../README.md) | [开发指南](DEVELOPMENT.md) | [贡献指南](../CONTRIBUTING.md)
  
</div>
