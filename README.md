# Phinix-Rework-Common

**Phinix Rework 共享层**：游戏无关的网络传输、身份认证、用户管理模型、扩展加载运行时及 Chat/Trade 中立契约源码。

**Phinix Rework Shared Layer**: Game-independent networking, authentication, user management, extension runtime, and neutral Chat/Trade contract source.

> [!NOTE]
> 本项目统一称为 **Phinix Rework**，与原 Phinix 项目区分。本仓库为共享源码库，不作为可执行宿主，不直接分发 RimWorld 模组安装包，也不提供独立的 Phinix NuGet SDK 通道。
> This project is named **Phinix Rework**, distinct from the original Phinix. This repository contains shared source only; it is not an executable host, produces no RimWorld mod package, and provides no standalone Phinix NuGet SDK channel.

---

## 仓库职责与关联 / Responsibilities & Relations

| 仓库 / Repository | 角色 / Role | 职责 / Responsibility |
| :--- | :--- | :--- |
| [Phinix-Rework-Common](https://github.com/HunYuan2333/Phinix-Rework-Common/tree/dev) | 共享层（本仓库） | 游戏无关的底层核心逻辑、网络传输、加密认证与协议契约 |
| [Phinix-Rework](https://github.com/HunYuan2333/Phinix-Rework/tree/dev) | 客户端 | RimWorld 1.6 模组客户端宿主、游戏内 UI 及客户端插件 |
| [Phinix-Rework-Server](https://github.com/HunYuan2333/Phinix-Rework-Server/tree/dev) | 服务端 | 独立的专用服务器程序、服务端插件及 Docker 容器镜像 |

客户端和服务端均将本仓库作为 Git 子模块固定在 `Dependencies/Phinix.Common`，通过项目引用直接参与编译构建。仓库更名不改动既有程序集名称、命名空间、协议标识或子模块目录名。

Both the Client and Server repositories pin this repository as a Git submodule at `Dependencies/Phinix.Common` and reference its projects directly during compilation. Repository names do not alter existing assembly names, namespaces, wire protocol identifiers, or the submodule directory name.

---

## 获取源码 / Source Acquisition

克隆本仓库及递归子模块 / Clone the repository with recursive submodules:

```bash
git clone --branch dev --recurse-submodules https://github.com/HunYuan2333/Phinix-Rework-Common.git
cd Phinix-Rework-Common
```

已有检出递归更新固定子模块 / For an existing checkout:

```bash
git submodule update --init --recursive
```

> [!IMPORTANT]
> 获取与构建使用固定提交的 gitlink，**切勿使用** `git submodule update --remote`。protobuf 为嵌套子模块（`Dependencies/protobuf`），必须递归（`--recursive`）初始化。
> Acquisition uses pinned gitlinks; **do not use** `git submodule update --remote`. protobuf is a nested submodule (`Dependencies/protobuf`) and must be initialized recursively.

---

## 编译与测试 / Build & Test

编译需要 .NET 10 SDK 和可还原的 NuGet 依赖；各工程同时面向 `net472` 与 `net10.0` 进行多目标编译，不需要 RimWorld 游戏 DLL。

Requires .NET 10 SDK and restored NuGet dependencies. Each project multi-targets `net472` and `net10.0` without RimWorld game assemblies.

在仓库根目录按依赖顺序编译全部六个共享工程 / Build all six shared projects in order:

```bash
bash <<'BUILD'
set -e
for project in \
  Common/Utils/Utils.csproj \
  Common/Connections/Connections.csproj \
  Common/Authentication/Authentication.csproj \
  Common/UserManagement/UserManagement.csproj \
  Extensions/Chat/Contracts/ChatExtension.csproj \
  Extensions/Trade/Contracts/TradeExtension.csproj
do
  dotnet build "$project" --configuration Release -p:BuildInParallel=false -m:1
done
BUILD
```

运行底层运行时回归测试（注意：自动化测试通过仅用于验证底层逻辑，不代表游戏内运行验证）：
Run core runtime regression tests (note: automated tests verify runtime logic and do not certify in-game behavior):

```bash
dotnet run --project Tests/Phase35CoreRuntimeTests/Phase35CoreRuntimeTests.csproj --configuration Release
```

---

## 输出位置 / Output Location

每个工程分别输出到自身的 `bin/Release/net472/` 与 `bin/Release/net10.0/` 目录下。例如：
Each project writes to its own target-framework output directory, for example:

```text
Common/Utils/bin/Release/net472/Utils.dll
Common/Utils/bin/Release/net10.0/Utils.dll
Common/Connections/bin/Release/net10.0/Connections.dll
Common/Authentication/bin/Release/net10.0/Authentication.dll
Common/UserManagement/bin/Release/net10.0/UserManagement.dll
Extensions/Chat/Contracts/bin/Release/net10.0/ChatExtension.dll
Extensions/Trade/Contracts/bin/Release/net10.0/TradeExtension.dll
```

### 产物与分发说明 / Artifact & Distribution Notes

- **不生成独立模组包**：共享层不生成游戏模组安装包。客户端与服务端在构建时会自动编译其固定子模块中的源码并打包所需资产，不需要手动从相邻的 Common 检出目录复制 DLL。
- **契约隔离**：中立的 `net472` Chat/Trade 契约库由端点构建自包含处理，不应与客户端完整同名契约混杂分发。
- **No standalone mod package**: The shared layer produces no mod package. Endpoint builds package their own pinned dependencies; do not copy DLLs from a neighboring checkout.
- **Contract boundary**: Do not distribute the neutral `net472` Chat/Trade contract assemblies alongside full client contract assemblies.

---

## 维护说明 / Notes

- 普通构建会按 `nuget.config` 还原依赖。离线构建依赖本地已准备的 SDK、目标包与 NuGet 缓存。
- NU1900 警告表示 NuGet 漏洞数据库连接失败，属于网络建议提示，不等于编译错误，也不代表已完成安全审计。
- protobuf 为外部固定依赖，请勿擅自修改 vendored protobuf 的 SDK 锁定文件与源码。
- Builds restore dependencies through `nuget.config`. Offline builds require preinstalled SDKs, targeting packs, and cached packages.
- NU1900 reports unavailable vulnerability data; it is an advisory notice, not a compile failure or a completed security audit.
- Do not modify vendored protobuf SDK pins or submodule configuration.
