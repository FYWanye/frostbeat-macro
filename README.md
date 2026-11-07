# 冰与火之宏 (FrostBeat)

《冰与火之舞》(A Dance of Fire and Ice) 的自动演奏宏工具。通过读取游戏内存中的谱面时间线，按真人手法自动击键，支持常规、变速、旋转与 Hold 谱面。

> 本项目为个人学习与逆向研究用途。使用宏可能违反游戏的最终用户协议，请自行承担使用后果。

## 功能特性

- **自动击键**：读取谱面事件并按时间线精确调度按键
- **仿真人手法**：逐档轮指、加键速度、随机延迟、左右手交替
- **顺序轮换**：按填写顺序轮换按键，忙碌按键自动跳过
- **Hold 支持**：长按砖块按谱面松开
- **安全安装**：安装/卸载读取模块时自动备份并校验原始游戏文件
- **悬浮窗 UI**：可缩放、可调透明度、置顶显示

## 使用说明

1. 启动游戏一次以让程序定位游戏目录（或手动选择目录）
2. 点击「安装 / 更新模块」，程序会向游戏程序集注入读取模块（原文件自动备份）
3. 重启游戏，点击「待命 F8」，切回游戏，手动按首键开始自动演奏
4. `F8` 待命 / 停止，`F9` 停止，`Esc` 停止，`Ctrl+F10` 展开/收起设置

## 快捷键

| 按键 | 功能 |
|------|------|
| F8 | 待命 / 停止 |
| F9 | 停止 |
| Esc | 停止 |
| Ctrl+F10 | 展开 / 收起设置 |

## 构建

依赖：
- Visual Studio 2022 Build Tools（含 Roslyn `csc.exe`）
- .NET Framework 4.8.1 Targeting Pack
- `Mono.Cecil.dll`（0.11.6.0，仓库内已包含）

```
build.bat
```

产物输出到 `build\FrostBeat.exe`。

## 目录结构

```
├── build.bat                    # 构建脚本
├── FrostBeat.exe                # 主程序（编译产物）
├── FrostBeat.exe.config         # 运行时配置（.NET Framework 4.8）
├── FrostBeat.Bridge.dll         # 注入到游戏内的读取桥接模块
├── Mono.Cecil.dll               # IL 字节码补丁库（0.11.6.0）
└── src/
    ├── FrostBeat.decompiled.cs  # 主程序 C# 源码
    ├── MainWindow.xaml          # 悬浮窗 UI
    └── FrostBeat.Bridge.decompiled.cs  # 桥接模块 C# 源码
```

## 崩溃修复说明

本分支对原程序进行了以下稳定性修复：

1. **全局异常处理**：挂接 `AppDomain.UnhandledException` 与 `ThreadException`，后台线程异常不再导致静默闪退，并写入 `%LOCALAPPDATA%\FrostBeat\frostbeat.log`。
2. **UI 异常节流**：`DispatcherUnhandledException` 弹窗增加 2 秒节流，避免周期异常导致弹窗风暴卡死界面。
3. **文件读取防御**：Steam 的 `libraryfolders.vdf` / `appmanifest.acf` 读取增加 try/catch，文件被锁或损坏不再导致启动崩溃。
4. **共享内存读取防御**：`Reader.Read` 与 `Dispose` 增加防御，共享内存撕裂/被释放时安全返回 null 而非逃逸异常到调度线程。
5. **调度线程降级**：工作线程优先级从 `Highest` 降为 `AboveNormal`，避免抢占系统资源导致整机卡顿。

## UI 重做说明

- 深色玻璃质感表面 + 渐变边框
- 「冰火」双色主题：冰蓝（左手/主操作）、火焰橙（右手/停止），呼应软件名
- 统一圆角按钮（悬停/按压反馈）、聚焦高亮的输入框、分节卡片
- 全部控件命名保持与代码一致，功能不变

## 免责声明

本项目仅供技术学习与研究。使用者需自行确保遵守游戏及相关平台的服务条款。作者不对任何因使用本软件导致的后果负责。
