# 连接等待补丁

`connect_probe.c` 与三个 `connect_*.h` 生成 x86 连接等待模块。`connect_signature.h` 固化已验证的调用点签名及导入地址检查；修改支持布局时应同步更新签名测试。

构建需要 LLVM-MinGW 或 MinGW-w64 的 x86 Windows 编译器。可用 `-ToolchainDirectory` 指定编译器所在的 `bin` 目录；省略时从 PATH 查找 `i686-w64-mingw32-gcc` 或 `i686-w64-mingw32-clang`。

```powershell
.\build.ps1 -ToolchainDirectory <toolchain-bin>
.\test-native.ps1 -ToolchainDirectory <toolchain-bin>
```

`build.ps1` 构建 DLL 并运行逻辑、签名测试。`test-native.ps1` 另外运行回环连接、透传及序号导入测试，需要 PATH 中的 Python 和 `pefile`；可通过 `-PythonPath` 指定 Python。

默认输出到项目的 `artifacts/native/connect`，可用 `-OutputDirectory` 更改。生产产物名为 `chuni_connect_probe.dll`；正式资源使用同一文件的名称 `../connect_compat.dll`，不会被构建脚本覆盖。

当前正式资源 SHA256：`1312E4D4DE3B35E60FF2410C944AF828CAD24A7A04E33172F6483606754AFAD7`。生成该资源的工具链为 LLVM-MinGW `20260922-ucrt-x86_64`。更新正式资源时同步检查助手构建脚本、`GameLauncher.cs` 和启动器测试中的 Hash。
