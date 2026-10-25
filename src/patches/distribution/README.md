# 配信启动补丁

`daemon_registry_fix.c` 使用 `registry_signature.h` 的程序身份及调用点签名，仅对已验证的启动调用处理短暂的共享冲突。`sha256_local.h` 提供文件身份校验。签名已固化，常规构建不需要游戏文件或签名生成步骤。

构建需要 LLVM-MinGW 或 MinGW-w64 的 x64 Windows 编译器。可用 `-ToolchainDirectory` 指定编译器所在的 `bin` 目录；省略时从 PATH 查找 `x86_64-w64-mingw32-gcc` 或 `x86_64-w64-mingw32-clang`。

```powershell
.\build.ps1 -ToolchainDirectory <toolchain-bin>
```

构建后运行隔离内存测试，验证重试、调用参数、错误码、回滚和身份拒绝。默认测试不读取任何游戏文件。需要额外核对待支持文件身份时，可同时传入 `-MainImagePath` 与 `-HookImagePath`，或向测试程序传入这两个文件路径。

默认输出到项目的 `artifacts/native/distribution`，可用 `-OutputDirectory` 更改。脚本不覆盖正式资源 `../daemon_registry_fix.dll`。

当前正式资源 SHA256：`D4C4A57652FB6F4FDAE12FE3736A58A63E1DF5F42B0F38BA89877B64C3D4AB10`。生成该资源的工具链为 LLVM-MinGW `20260922-ucrt-x86_64`。更新正式资源时同步检查助手构建脚本、`GameLauncher.cs` 和启动器测试中的 Hash。
