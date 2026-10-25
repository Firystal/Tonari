# 贡献指南

欢迎为 Tonari 提交问题、修复和文档改进。

## 开发环境

- Windows 10／11
- .NET Framework 4.8 与 Windows PowerShell 5.1
- Node.js 20+、npm
- Python 3.10+

在项目根目录安装依赖并构建：

```powershell
npm ci
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\build.ps1
```

输出为 `dist/Tonari/Tonari.exe`。构建会校验两个嵌入补丁的 SHA-256。原生补丁的构建方法见 [连接等待补丁](src/patches/connect/README.md) 和 [配信启动补丁](src/patches/distribution/README.md)。

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/` | C#、WPF 界面、构建入口和资源 |
| `src/patches/` | 嵌入补丁及对应的原生源码 |
| `src/tests/` | 功能测试、隔离 UI 测试与输出管理 |
| 根目录的两份教程 `.md` | 基准机与接入机教程源文件 |
| `tools/` | 教程渲染、验证、打包和桌面部署脚本 |
| `dist/` | 生成的程序目录与 ZIP，不提交 |
| `artifacts/` | 测试报告、截图及原生构建产物，不提交 |
| `profiles/` | 本地房间配置，不提交、不打包 |

## 测试

根据修改范围运行对应测试；涉及配置保存或游戏启动流程时运行全部功能测试：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\tests\run-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\tests\run-game-launcher-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\tests\run-preparation-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\tests\run-test-role-tests.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src\tests\run-zerotier-readonly-tests.ps1
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File src\tests\run-ui-tests.ps1
```

输出位于 `artifacts/tests/`。UI 测试使用隔离目录与模拟回调，生成不同窗口尺寸的布局截图。

## 教程

直接编辑根目录的 [基准机教程](01-基准机从零建房教程.md) 或 [接入机教程](02-接入机从零加入教程.md)，然后渲染并检查：

```powershell
npm run docs
npx playwright install chromium
npm run test:docs
```

生成的 `基准机教程.html` 与 `接入机教程.html` 位于 `dist/Tonari/`。检查报告和截图位于 `artifacts/guides/`。使用已安装的 Edge 检查时，可设置环境变量 `CHUNI_BROWSER_CHANNEL=msedge`。

## 打包与本地部署

完成构建后运行：

```powershell
python tools\package.py
```

脚本渲染教程、复制源码，并生成 `dist/Tonari/` 和 `dist/Tonari.zip`，随后校验发布清单与压缩包中的 SHA-256。发布包的 `source/` 包含对应源码和构建工具。

`python tools\package.py --check` 可检查打包输入。需要更新桌面副本时，关闭 Tonari 后运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\deploy.ps1
```

加上 `-WhatIf` 可预览部署目标。部署脚本依据上次发布清单移除已撤下的文件。

## 提交修改

- 保持改动集中，说明解决的问题、最终行为和验证结果。
- 界面调整应检查基准机、接入机和单机三种状态，并同步相关教程。
- 不提交游戏文件、个人房间配置、账号凭据或诊断日志。
- 遵循 `.editorconfig`：C#、XAML 和 PowerShell 使用 UTF-8 BOM、CRLF；Markdown、Python、JavaScript、JSON、C 与头文件使用 UTF-8、LF。

提交的代码与文档采用项目的 [GPL-3.0 许可证](LICENSE)。
