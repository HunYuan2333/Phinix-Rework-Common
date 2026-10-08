# Phinix-Common

Phinix 共享源码：游戏无关的网络、认证、用户管理、框架运行时及 Chat/Trade 中立契约。

Shared game-independent networking, authentication, user management, framework runtime and neutral Chat/Trade contracts. Client and Server consume exact commits through a submodule and ProjectReference. This repository is not an executable host or a standalone client SDK.

> 以下构建命令在独立 checkout 中验证通过；构建不等于游戏验收。迁移未改变网络协议、存档格式或物品所有权规则。拆分前完整源码保留在原 Rework 仓的 `codex/pre-split-20261008` 分支。
> Builds were verified in independent checkouts and do not certify in-game behavior. Wire identities, persistence formats and item-ownership rules remain unchanged. The complete pre-split source is preserved on the original Rework repository branch codex/pre-split-20261008.

## 获取源码 / Checkout

正式远端就绪后 / Once the reviewed remote commits are published:

```bash
git clone --recurse-submodules https://github.com/HunYuan2333/Phinix-Common.git
cd Phinix-Common
```

已有 checkout 可执行 / For an existing checkout:

```bash
git submodule update --init --recursive
```

正常获取使用固定 gitlink，不使用 `update --remote`。protobuf 为嵌套子模块，需递归初始化。
Acquisition uses pinned gitlinks; do not use `update --remote`. Initialize nested protobuf recursively.

## 编译 / Build

需要 .NET 10 SDK 和可还原的 NuGet 依赖；各工程同时构建 `net472` 与 `net10.0`，不需要游戏 DLL。
Requires .NET 10 SDK and restored NuGet dependencies. Each project builds net472 and net10.0 without game assemblies.

在仓库根目录运行以下 Bash 块，编译全部六个共享工程，任一步失败即停止：
Run this Bash block from the repository root to build all six projects, stopping on failure:

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

## 输出 / Output

每个工程输出到自己的 `bin/Release/net472/` 和 `bin/Release/net10.0/`。例如：
Each project writes to its own target-framework output, for example:

```text
Common/Utils/bin/Release/net472/Utils.dll
Common/Utils/bin/Release/net10.0/Utils.dll
```

Shared 不生成游戏安装包。两端编译会自动构建并分发其固定子模块的依赖，不需手工复制旁边 Common 仓的 DLL。中立 net472 Chat/Trade 契约不应与客户端完整同名契约一起分发。
Shared produces no mod package. Endpoint builds package their own pinned dependencies; do not copy DLLs from a neighboring checkout. Do not distribute the neutral net472 Chat/Trade assemblies alongside the full same-name client contract assemblies.

## 说明 / Notes

- 普通构建会按 `nuget.config` 还原依赖。离线验证依赖本机已准备的 SDK/框架包与 NuGet 缓存，不保证空机器完全离线。
- NU1900 表示 NuGet 漏洞信息获取失败；不等于编译错误，也不代表已完成漏洞审计。
- 保留原有相关历史，不擅自修改 protobuf 的 SDK 固定文件。根 LICENSE 在原仓缺失，迁移候选未自行添加新的许可证；正式发布说明仍需明确。

Normal builds restore through nuget.config. Offline validation requires preinstalled SDK/framework packs and cached packages. NU1900 reports unavailable vulnerability data, not a completed audit. Relevant history is preserved; do not patch vendored protobuf SDK pins. No new root license was invented during extraction.
