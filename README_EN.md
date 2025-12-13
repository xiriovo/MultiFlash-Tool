<div align="center">
  <img src="assets/logo.jpg" alt="MultiFlash Tool Logo" width="300"/>
  
  # MultiFlash Tool
  
  **An Open-Source Multi-Function Android Flashing Tool**
  
  Supports Qualcomm EDL (9008) Mode and Fastboot Mode
  
  [![License](https://img.shields.io/badge/License-Non--Commercial-red.svg)](LICENSE)
  [![.NET](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
  [![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)
  
  [中文文档](README.md) | English
  
</div>

---

## ⚠️ License Notice

This project is licensed under a **Non-Commercial License**. **Any form of commercial use is prohibited.**

- ❌ No selling
- ❌ No use in commercial products
- ❌ No use for profit
- ✅ Free for learning and research
- ✅ Can be modified and distributed (must maintain same restrictions)

## ✨ Features

### Core Functions

- 📱 **Smart Device Detection**
  - Auto-detect ADB/Fastboot/EDL devices
  - Real-time device status monitoring
  - Multi-device management

- 🔧 **EDL Mode Flashing**
  - Support Qualcomm 9008 mode flashing
  - Sahara protocol communication
  - Firehose protocol flashing
  - GPT partition table backup/restore
  - Memory read/write operations

- ⚡ **Fastboot Enhancement**
  - Partition read/write operations
  - OEM unlock/relock
  - Device information query
  - Custom command execution

- 📦 **Firmware Tools**
  - Payload.bin extraction
  - Super partition merge
  - Sparse image handling
  - Partition image extraction

### Advanced Features

- 🔐 **Security Authentication** - Cloud authorization verification
- 📝 **Detailed Logging** - Operation log recording and export
- 🌐 **Multi-language Support** - Chinese interface
- 🎨 **Modern UI** - Based on AntdUI framework

## 📋 System Requirements

### Minimum Configuration
- **Operating System**: Windows 10 (64-bit) or higher
- **Runtime**: .NET Framework 4.8 or higher
- **Memory**: 4GB RAM
- **Storage**: 500MB available space

### Driver Requirements
- **Qualcomm EDL Driver**: For 9008 mode (required)
- **ADB Driver**: For ADB debugging
- **Fastboot Driver**: For Fastboot mode

## 🚀 Quick Start

### Installation Steps

1. **Download Program**
   - Download latest version from [Releases](../../releases) page
   - Extract to any directory (English path recommended)

2. **Install Drivers**
   - Install Qualcomm EDL driver (9008 mode)
   - Install ADB and Fastboot drivers

3. **Run Program**
   ```
   MultiFlash Tool.exe
   ```

4. **Connect Device**
   - Connect device via USB
   - Program will auto-detect device type
   - Select corresponding operation mode

### Usage Examples

#### EDL Mode Flashing
1. Device enters 9008 mode
2. Select Programmer file (.mbn/.elf)
3. Select firmware package or partition image
4. Click "Start Flashing"

#### Fastboot Operations
1. Device enters Fastboot mode
2. Select partition to operate
3. Execute read/write/erase operations

#### Payload Extraction
1. Select OTA package (.zip)
2. Select output directory
3. Click "Extract Payload"

## 📚 Documentation

- **[Development Guide](DEVELOPMENT.md)** - Project structure and development standards
- **[Contributing Guide](CONTRIBUTING.md)** - How to contribute to the project
- **[Changelog](CHANGELOG.md)** - Version update history

## 🤝 Contributing

Issues and Pull Requests are welcome!

### How to Contribute
1. Fork this repository
2. Create feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit changes (`git commit -m 'Add: some feature'`)
4. Push to branch (`git push origin feature/AmazingFeature`)
5. Submit Pull Request

See [CONTRIBUTING.md](CONTRIBUTING.md) for complete contribution guidelines.

## 🛠️ Tech Stack

- **UI Framework**: [AntdUI](https://gitee.com/antdui/AntdUI) 2.2.1
- **Compression**: SharpZipLib 1.4.2
- **JSON**: System.Text.Json 8.0.5 / Newtonsoft.Json 13.0.4
- **Protobuf**: Google.Protobuf 3.17.3
- **Encryption**: Fody / Costura

## ❓ FAQ

### Device Not Detected?
- Check if drivers are correctly installed
- Try different USB port
- Confirm device is in correct mode

### EDL Mode Flashing Failed?
- Confirm Programmer file matches device
- Check firmware package integrity
- Review log files for errors

### Permission Denied?
- Run program as administrator
- Check if antivirus software is blocking

## ⚠️ Disclaimer

**Using this tool carries risks and may brick your device or cause data loss.**

- ⚠️ Always backup important data before operations
- ⚠️ Understand the risks of flashing operations
- ⚠️ Improper operations may prevent device from booting
- ⚠️ This tool is for learning and research purposes only

**Developers are not responsible for any losses caused by using this tool.**

## 📄 License

This project is licensed under a **Non-Commercial License** - see [LICENSE](LICENSE) file

- ✅ Allowed for personal learning and research
- ✅ Can be modified and distributed (must maintain same license)
- ❌ Prohibited for any commercial use
- ❌ Prohibited from selling or using for profit

## 🌟 Star History

If this project helps you, please give it a Star ⭐

## 📧 Contact

- **Issues**: [GitHub Issues](../../issues)
- **Discussions**: [GitHub Discussions](../../discussions)

---

<div align="center">
  Made with ❤️ by MultiFlash Tool Team
  <br>
  Copyright © 2024 MultiFlash Tool. All rights reserved.
</div>
