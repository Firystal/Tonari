# Tonari

> **联机需要 [ZeroTier](https://www.zerotier.com/download/)。** 请先安装并启动 ZeroTier，确保参与联机的电脑已加入同一网络并完成授权和地址分配。

Tonari 是面向 Windows 的 CHUNITHM 店内联机助手，支持一个基准机与最多三个接入机。通过统一界面完成房间配置、机器状态切换、网络检测与游戏启动。

## 功能

- **四机房间**：分配机号、导入和导出房间配置。
- **游戏配置**：选择游戏 `bin` 文件夹，读取 `segatools.ini`、`start.bat` 和当前机器状态。
- **模式切换**：在单机与联机之间切换，同步游戏配置与基准机／接入机状态。
- **网络检查**：查看地址、检测房间设备、配置防火墙和导出诊断信息。
- **启动补丁**：按需启用连接等待修复和配信启动修复，并从助手启动游戏。

## 开始使用

运行环境：**Windows 10／11、.NET Framework 4.8、ZeroTier**。各台电脑应能独立运行相同版本和内容的 CHUNITHM。

1. 从本仓库的 Releases 下载 `Tonari.zip` 并完整解压，运行 `Tonari.exe`。
2. 按下表选择教程，完成 ZeroTier 网络、房间和游戏配置。
3. 先启动基准机，进入标题或演示画面后，再启动接入机。

| 本机用途 | 教程 |
| --- | --- |
| 创建房间，作为 1 号基准机 | [基准机：从零建房](01-基准机从零建房教程.md) |
| 加入已有房间，作为 2～4 号接入机 | [接入机：从零加入](02-接入机从零加入教程.md) |

**教程有两种打开方式：** 直接打开发布包中与 `Tonari.exe` 同目录的 `基准机教程.html` 或 `接入机教程.html`；也可以在软件的 **帮助与诊断** 页面打开对应教程。GitHub 上可通过上表阅读 Markdown 版本。

日常使用时，在首页切换单机或联机，等待状态栏显示保存完成后启动游戏。修改房间、机号、游戏目录或补丁选项后，点击 **保存房间设置**。

## 从源码构建

需要 Windows、.NET Framework 4.8、Windows PowerShell 5.1、Node.js 20+ 和 Python 3.10+。在项目根目录运行；使用发布包中的源码时，先进入 `source` 文件夹：

```powershell
npm ci
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
python tools\package.py
```

程序输出到 `dist/Tonari/Tonari.exe`，发布包为 `dist/Tonari.zip`。测试、教程渲染和开发约定见 [贡献指南](CONTRIBUTING.md)。

## 反馈与贡献

欢迎通过 Issues 提交问题和功能建议，或通过 Pull Request 改进代码与文档。报告问题时，请提供软件版本、机器角色、复现步骤及相关日志。

## 许可证

本项目采用 [GNU General Public License v3.0](LICENSE)。
