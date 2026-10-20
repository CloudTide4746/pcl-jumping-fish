# PCL-跳跳鱼小队专用版

一个**最小单文件 Minecraft 启动器**，闭环覆盖 **下载游戏 → 安装 Java → 启动游戏**。

本项目是对 [Plain Craft Launcher 2](https://github.com/Meloong-Git/PCL) v2.13.1.1 (build 408) 的**功能级逆向重构**：通过 ILSpy 反编译 + 运行时证据交叉验证，还原其三段闭环的可观察契约，再用一个独立的 C# 单文件从零实现。**不包含任何 PCL 的原始源代码**。

## 特性

- **单文件交付**：编译产物就是一个 `.exe`（约 80 KB），依赖仅 .NET Framework 4.8（Windows 10/11 自带），无需安装运行时
- **下载游戏**：Mojang 官方版本清单 + BMCLAPI 镜像自动回退，支持主 jar / libraries / 资源对象 / 资源索引全量下载与 SHA-1 校验
- **安装 Java**：自动探测本机 Java，按实例推导版本要求，缺失时从 Mojang 拉取官方运行时（`piston-meta` 逐文件布局）
- **启动游戏**：完整复刻 PCL 的 JVM 参数组装、占位符替换、继承链合并、natives 解压、进程环境隔离
- **双击即用**：无参数运行时进入交互菜单，自动列出 `versions/` 下所有实例

## 快速开始

1. 下载 [`PCL-跳跳鱼小队专用版.exe`](../../releases/latest)
2. 放到你的 Minecraft 目录旁边（默认会读取同目录下的 `.minecraft`）
3. 双击运行，从菜单里选一个版本启动

```
D:\MineCraft\
├── PCL-跳跳鱼小队专用版.exe
└── .minecraft\
    └── versions\
        └── 愚者\
```

## 命令行用法

```text
PCL-跳跳鱼小队专用版                    进入交互菜单
PCL-跳跳鱼小队专用版 --list             列出已安装版本
PCL-跳跳鱼小队专用版 --install <版本>    下载并安装原版（如 1.20.1）
PCL-跳跳鱼小队专用版 --fix <实例>        补全已有实例的缺失文件
PCL-跳跳鱼小队专用版 --java             扫描本机 Java
PCL-跳跳鱼小队专用版 --java <实例>       查看该实例的 Java 要求与选择结果
PCL-跳跳鱼小队专用版 --launch <实例>     启动
PCL-跳跳鱼小队专用版 --launch <实例> --dry-run   只打印启动命令
```

常用选项：

| 选项 | 说明 |
|---|---|
| `--mc <目录>` | `.minecraft` 目录（默认：exe 同目录下的 `.minecraft`） |
| `--user <名字>` | 离线登录用户名（默认 `Player`） |
| `--ram <MB>` | 最大堆内存（默认按物理内存与 mods 数自动估算） |
| `--gc <0-4>` | GC 策略，4 = G1 增强（默认，与 PCL 一致） |
| `--server <地址>` | 启动后自动进服 |
| `--fullscreen` | 全屏 |
| `--width` / `--height` | 窗口尺寸（默认 854×480） |
| `--jvm` / `--game-args` | 追加参数 |
| `--verify` | 按 SHA-1 校验已有文件（较慢） |
| `--no-mirror` | 不使用 BMCLAPI 镜像 |
| `--no-java-download` | 禁止自动下载 Java |
| `--dry-run` | 只打印启动命令，不真正启动 |
| `--wait` | 等待游戏退出并返回其退出码 |
| `--verbose` / `--log` | 输出调试信息 / 写日志文件 |

## 构建

需要 .NET SDK（`net48` 目标框架，Windows 上开箱可用）：

```bash
dotnet build -c Release
```

产物：`bin/Release/net48/PCL-跳跳鱼小队专用版.exe`

## 逆向结论摘要

| 环节 | 还原的关键契约 |
|---|---|
| 版本清单 | `launchermeta.mojang.com`；`_`→`-`，除 `1.0` 外去尾部 `.0` |
| 继承合并 | 子 libraries 在前、父 libraries 追加在后；其余字段父为底、子覆盖 |
| 主 jar 语义 | Forge 系且原版 ≥ 17 时**不下沉**继承链，而是使用实例自身的 jar |
| Java 要求 | 按发布日/版本号分档推导，再被加载器类型收窄 |
| Java 运行时 | `all.json` → `windows-x64` → 逐文件 `downloads.raw`，跳过 3 个黑名单 sha1 |
| JVM 参数 | GC 增强模式、编码（按 Java 探测的 `native.encoding`）、`-Dlog4j2.formatMsgNoLookups=true` |
| 进程环境 | `appdata` 改写为 MC 目录以隔离读写；`Path` 前置 Java 目录 |
| 离线 UUID | 基于 `5381` 的哈希后 XOR `0xA98F501BC684032F`，再按 UUID v3 位布局改写 |

完整的证据账本（PCL 源文件与行号）保存在开发过程记录中，本仓库只保留结论。

## 已知差异

- **登录**：仅实现离线登录，未复刻 Microsoft OAuth 六步流程
- **Java 选择**：PCL 按目录枚举顺序取首个满足项；本实现改为**升序取最低满足版本**，避免 1.20.1 + Forge 误选 Java 25/26 崩溃
- **JLW**：GBK 环境下 PCL 会禁用 JavaWrapper，本实现同样不引入 `JavaWrapper.jar`
- **下载调度**：未逐行复刻 PCL 的多源淘汰调度器，只保留 Range 分段、SHA-1 校验、镜像回退等可观察契约

## 许可与声明

本项目为独立实现，与 Mojang Studios、Microsoft、Plain Craft Launcher 官方均无关联。
"Minecraft" 是 Mojang Studios 的商标。请仅用于你拥有正版授权的游戏。

PCL 本身采用 [MIT License](https://github.com/Meloong-Git/PCL/blob/main/LICENSE)；本项目的开发方式为**行为级逆向 + 独立实现**，不复制其代码。
